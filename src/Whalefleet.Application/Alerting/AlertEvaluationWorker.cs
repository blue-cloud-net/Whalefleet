using System.Globalization;

using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Notify;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 告警评估后台工作器。
/// </summary>
/// <remarks>
/// <para><b>两类评估节奏</b>：主机离线与登录失败激增按 <see cref="AlertsOptions.EvaluationIntervalSeconds"/>
/// 周期检查；容器指标复用 <see cref="IContainerStatsSampler"/> 的长期订阅，订阅集合按
/// <see cref="AlertsOptions.ContainerResyncSeconds"/> 重新同步。</para>
/// <para><b>为什么复用采样器而不是自己开流</b>：采样器已经把「一台容器一条 Docker 流」做成进程内共享。
/// 评估器再开一份，同一个容器会被两条流同时采样——既浪费，也会让「页面上的指标」与「告警判定」
/// 的数字对不上。</para>
/// <para><b>所有写库都通过作用域</b>：工作器是 Singleton，仓储是 Scoped。
/// 每个触发点自建作用域，避免持有请求级服务。</para>
/// <para><b>best-effort</b>：单个主机 / 容器失败只记日志，不终止循环——
/// 一台机器连不上不该让其它机器的告警一起停摆。</para>
/// </remarks>
public sealed class AlertEvaluationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IContainerStatsSampler _sampler;
    private readonly IOptions<AlertsOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertEvaluationWorker> _logger;

    private readonly object _gate = new();

    /// <summary>
    /// 容器监控订阅（键 = <c>host|containerId</c>）。
    /// </summary>
    private readonly Dictionary<string, MonitorEntry> _monitors = new(StringComparer.Ordinal);

    /// <summary>
    /// 连续命中计数（键 = <c>ruleId|targetKey</c>）。
    /// </summary>
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    private CancellationToken _stoppingToken;
    private DateTime _lastTrim = DateTime.MinValue;

    /// <summary>
    /// 初始化工作器。
    /// </summary>
    public AlertEvaluationWorker(
        IServiceScopeFactory scopes,
        IContainerStatsSampler sampler,
        IOptions<AlertsOptions> options,
        TimeProvider clock,
        ILogger<AlertEvaluationWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _sampler = sampler;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        var options = _options.Value;

        var periodic = this.RunPeriodicAsync(
            TimeSpan.FromSeconds(options.EvaluationIntervalSeconds),
            this.EvaluateHostsAndLoginAsync,
            stoppingToken);

        var resync = this.RunPeriodicAsync(
            TimeSpan.FromSeconds(options.ContainerResyncSeconds),
            this.SyncMonitorsAsync,
            stoppingToken);

        try
        {
            await Task.WhenAll(periodic, resync).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
        finally
        {
            this.StopAllMonitors();
        }
    }

    /// <summary>
    /// 周期性执行一个动作。
    /// </summary>
    private async Task RunPeriodicAsync(
        TimeSpan interval,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "告警评估首轮执行失败，继续按周期重试");
        }

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await action(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "告警评估周期任务失败，继续下一轮");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止。
        }
    }

    /// <summary>
    /// 评估主机离线与登录失败激增，并做每日保留期裁剪。
    /// </summary>
    private async Task EvaluateHostsAndLoginAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();

        var hosts = await scope.ServiceProvider
            .GetRequiredService<IHostRepository>()
            .QueryAllListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rules = await scope.ServiceProvider
            .GetRequiredService<IAlertRuleRepository>()
            .QueryAllListAsync(cancellationToken)
            .ConfigureAwait(false);

        var enabled = rules.Where(rule => rule.Enabled).ToArray();

        await this.EvaluateHostOfflineAsync(scope.ServiceProvider, hosts, enabled, cancellationToken)
            .ConfigureAwait(false);

        await this.EvaluateLoginSurgeAsync(scope.ServiceProvider, enabled, cancellationToken)
            .ConfigureAwait(false);

        await this.TrimAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 评估主机离线。
    /// </summary>
    private async Task EvaluateHostOfflineAsync(
        IServiceProvider services,
        IReadOnlyList<DockerHost> hosts,
        IReadOnlyList<AlertRule> enabledRules,
        CancellationToken cancellationToken)
    {
        var rules = enabledRules.Where(rule => rule.Kind == AlertKind.HostOffline).ToArray();

        if (rules.Length == 0)
        {
            return;
        }

        var accessor = services.GetRequiredService<IDockerHostAccessor>();

        foreach (var host in hosts.Where(host => host.Enabled))
        {
            var rule = rules.FirstOrDefault(rule => rule.HostId is null || rule.HostId == host.Id);

            if (rule is null)
            {
                continue;
            }

            var targetKey = HostTargetKey(host);
            var counterKey = CounterKey(rule, targetKey);

            bool online;

            try
            {
                online = await accessor
                    .TestConnectionAsync(DockerHostConnection.From(host), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 探测自身失败按离线处理：这正是我们要发现的事
                _logger.LogDebug(exception, "主机 {Host} 连通性探测失败", host.Name);
                online = false;
            }

            if (online)
            {
                this.ResetCounter(counterKey);
                continue;
            }

            if (this.Increment(counterKey) < rule.ConsecutiveSamples)
            {
                continue;
            }

            await this.TryFireAsync(
                services,
                rule,
                targetKey,
                rule.Severity,
                metricValue: null,
                thresholdValue: null,
                host,
                containerId: null,
                containerName: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 评估登录失败激增。
    /// </summary>
    private async Task EvaluateLoginSurgeAsync(
        IServiceProvider services,
        IReadOnlyList<AlertRule> enabledRules,
        CancellationToken cancellationToken)
    {
        var rules = enabledRules.Where(rule => rule.Kind == AlertKind.LoginFailureSurge).ToArray();

        if (rules.Length == 0)
        {
            return;
        }

        var audit = services.GetRequiredService<IAuditLogRepository>();
        var now = _clock.GetUtcNow().UtcDateTime;

        foreach (var rule in rules)
        {
            var window = rule.WindowSeconds ?? 300;
            var from = now.AddSeconds(-window);

            var failures = await audit.CountAsync(
                new AuditLogFilter
                {
                    Category = AuditCategory.Authentication,
                    Action = AuditActions.Login,
                    Outcome = AuditOutcome.Failure,
                    From = from,
                    To = now,
                },
                cancellationToken).ConfigureAwait(false);

            var denied = await audit.CountAsync(
                new AuditLogFilter
                {
                    Category = AuditCategory.Authentication,
                    Action = AuditActions.Login,
                    Outcome = AuditOutcome.Denied,
                    From = from,
                    To = now,
                },
                cancellationToken).ConfigureAwait(false);

            var total = failures + denied;
            var severity = rule.ResolveSeverity(total);
            var counterKey = CounterKey(rule, "global");

            if (severity is null)
            {
                this.ResetCounter(counterKey);
                continue;
            }

            if (this.Increment(counterKey) < rule.ConsecutiveSamples)
            {
                continue;
            }

            await this.TryFireAsync(
                services,
                rule,
                "global",
                severity.Value,
                total,
                ThresholdOf(rule, severity.Value),
                host: null,
                containerId: null,
                containerName: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 同步容器指标订阅集合。
    /// </summary>
    private async Task SyncMonitorsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();

        var hosts = await scope.ServiceProvider
            .GetRequiredService<IHostRepository>()
            .QueryAllListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rules = (await scope.ServiceProvider
            .GetRequiredService<IAlertRuleRepository>()
            .QueryAllListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Where(rule => rule.Enabled && AlertKindText.IsContainerMetricKind(rule.Kind))
            .ToArray();

        if (rules.Length == 0)
        {
            this.StopAllMonitors();
            return;
        }

        var accessor = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();
        var maxPerHost = _options.Value.MaxMonitoredContainersPerHost;
        var desired = new Dictionary<string, MonitorEntry>(StringComparer.Ordinal);

        foreach (var host in hosts.Where(host => host.Enabled))
        {
            var hostRules = rules.Where(rule => rule.HostId is null || rule.HostId == host.Id).ToArray();

            if (hostRules.Length == 0)
            {
                continue;
            }

            try
            {
                var client = await accessor.GetClientAsync(host.Name, cancellationToken).ConfigureAwait(false);
                var containers = await client.Containers.ListContainersAsync(
                    new ContainersListParameters { All = false },
                    cancellationToken).ConfigureAwait(false);

                if (containers.Count > maxPerHost)
                {
                    _logger.LogWarning(
                        "主机 {Host} 有 {Count} 个运行中容器，超过监控上限 {Max}，只监控前 {Max} 个",
                        host.Name,
                        containers.Count,
                        maxPerHost,
                        maxPerHost);
                }

                foreach (var container in containers.Take(maxPerHost))
                {
                    var containerName = container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];
                    var key = MonitorKey(host.Name, container.ID);

                    desired[key] = new MonitorEntry
                    {
                        Host = host,
                        ContainerId = container.ID,
                        ContainerName = containerName,
                        Rules = hostRules,
                        Key = key,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 主机不可达由「主机离线」规则负责，这里只跳过
                _logger.LogDebug(exception, "枚举主机 {Host} 的容器以同步告警监控失败", host.Name);
            }
        }

        List<MonitorEntry> removed = [];
        List<MonitorEntry> added = [];

        lock (_gate)
        {
            foreach (var (key, entry) in _monitors.ToArray())
            {
                if (!desired.TryGetValue(key, out var latest))
                {
                    removed.Add(entry);
                    _monitors.Remove(key);
                }
                else
                {
                    // 规则集合可能在两次同步之间被改过：就地更新，不重启 stats 流
                    entry.Rules = latest.Rules;
                    entry.ContainerName = latest.ContainerName;
                }
            }

            foreach (var (key, entry) in desired)
            {
                if (_monitors.ContainsKey(key))
                {
                    continue;
                }

                _monitors[key] = entry;
                added.Add(entry);
            }
        }

        foreach (var entry in removed)
        {
            entry.Cancellation.Cancel();
        }

        foreach (var entry in added)
        {
            entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            entry.Task = this.RunMonitorAsync(entry, entry.Cancellation.Token);
        }
    }

    /// <summary>
    /// 消费一个容器的采样流并评估指标型规则。
    /// </summary>
    private async Task RunMonitorAsync(MonitorEntry entry, CancellationToken cancellationToken)
    {
        var subscription = _sampler.Subscribe(entry.Host.Name, entry.ContainerId);

        try
        {
            await foreach (var sample in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var rule in entry.Rules)
                {
                    var value = ComputeValue(rule.Kind, sample);

                    if (value is null)
                    {
                        continue;
                    }

                    var targetKey = ContainerTargetKey(entry.Host, entry.ContainerId);
                    var counterKey = CounterKey(rule, targetKey);
                    var severity = rule.ResolveSeverity(value.Value);

                    if (severity is null)
                    {
                        this.ResetCounter(counterKey);
                        continue;
                    }

                    if (this.Increment(counterKey) < rule.ConsecutiveSamples)
                    {
                        continue;
                    }

                    using var scope = _scopes.CreateScope();

                    await this.TryFireAsync(
                        scope.ServiceProvider,
                        rule,
                        targetKey,
                        severity.Value,
                        value.Value,
                        ThresholdOf(rule, severity.Value),
                        entry.Host,
                        entry.ContainerId,
                        entry.ContainerName,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停止监控（容器消失 / 宿主停止）。
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "容器告警监控流异常结束（主机={Host}，容器={Container}）",
                entry.Host.Name,
                entry.ContainerName);
        }
        finally
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 触发一条告警（先判冷却，再写事件 / 通知并投递渠道）。
    /// </summary>
    private async Task TryFireAsync(
        IServiceProvider services,
        AlertRule rule,
        string targetKey,
        AlertSeverity severity,
        double? metricValue,
        double? thresholdValue,
        DockerHost? host,
        string? containerId,
        string? containerName,
        CancellationToken cancellationToken)
    {
        var events = services.GetRequiredService<IAlertEventRepository>();
        var now = _clock.GetUtcNow().UtcDateTime;

        var last = await events.GetLastOccurredAtAsync(rule.Id, targetKey, cancellationToken)
            .ConfigureAwait(false);

        if (last is { } lastTime && lastTime > now.AddSeconds(-rule.CooldownSeconds))
        {
            return;
        }

        var alertEvent = AlertEvent.Record(
            Guid.NewGuid(),
            rule.Id,
            rule.Kind,
            targetKey,
            host?.Id,
            host?.Name,
            containerId,
            containerName,
            severity,
            metricValue,
            thresholdValue,
            now);

        await events.AddAsync(alertEvent, cancellationToken).ConfigureAwait(false);

        var (messageKey, parameters) = BuildNotificationParams(
            rule,
            host?.Name,
            containerName,
            metricValue,
            thresholdValue);

        if (containerId is not null) parameters = new Dictionary<string, object?>(parameters) { ["containerId"] = containerId };
        var notifications = services.GetRequiredService<INotificationWriter>();

        await notifications.CreateAsync(
            new NotificationDraft(
                NotificationSourceKind.AlertFired,
                severity,
                messageKey,
                parameters,
                host?.Id,
                host?.Name,
                DedupeKey: $"alert:{rule.Id:N}:{targetKey}:{now:yyyyMMddHHmm}",
                now),
            cancellationToken).ConfigureAwait(false);

        var channelIds = await services
            .GetRequiredService<IAlertRuleChannelRepository>()
            .GetChannelIdsAsync(rule.Id, cancellationToken)
            .ConfigureAwait(false);

        var message = new AlertNotificationMessage(
            rule.Kind,
            severity,
            host?.Name,
            containerName,
            messageKey,
            BuildPlainText(rule, host?.Name, containerName, metricValue, thresholdValue),
            metricValue,
            thresholdValue,
            now);

        await services
            .GetRequiredService<NotificationDeliveryService>()
            .DeliverAsync(channelIds, message, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "告警触发（规则={RuleKind}，严重度={Severity}，目标={TargetKey}）",
            AlertKindText.ToText(rule.Kind),
            AlertSeverityText.ToText(severity),
            targetKey);
    }

    /// <summary>
    /// 每日裁剪告警事件与通知。
    /// </summary>
    private async Task TrimAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (_lastTrim != DateTime.MinValue && now - _lastTrim < TimeSpan.FromDays(1))
        {
            return;
        }

        var before = now.AddDays(-_options.Value.RetentionDays);

        await services.GetRequiredService<IAlertEventRepository>()
            .TrimAsync(before, cancellationToken).ConfigureAwait(false);

        await services.GetRequiredService<INotificationRepository>()
            .TrimAsync(before, cancellationToken).ConfigureAwait(false);

        _lastTrim = now;
    }

    /// <summary>
    /// 停掉全部监控。
    /// </summary>
    private void StopAllMonitors()
    {
        List<MonitorEntry> entries;

        lock (_gate)
        {
            entries = [.. _monitors.Values];
            _monitors.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Cancellation.Cancel();
        }
    }

    /// <summary>
    /// 取计数器并自增。
    /// </summary>
    private int Increment(string key)
    {
        lock (_gate)
        {
            var next = _counters.GetValueOrDefault(key) + 1;
            _counters[key] = next;
            return next;
        }
    }

    /// <summary>
    /// 复位计数器。
    /// </summary>
    private void ResetCounter(string key)
    {
        lock (_gate)
        {
            _counters.Remove(key);
        }
    }

    /// <summary>
    /// 计算采样值；该规则不适用时返回 <see langword="null"/>。
    /// </summary>
    private static double? ComputeValue(AlertKind kind, ContainerStatsSample sample) => kind switch
    {
        AlertKind.ContainerCpu => sample.CpuPercent,
        AlertKind.ContainerMemory => sample.MemoryLimit > 0
            ? (double)sample.MemoryUsage / sample.MemoryLimit * 100
            : null,
        AlertKind.ContainerNetwork => sample.NetworkRxBytesPerSecond is { } rx
            && sample.NetworkTxBytesPerSecond is { } tx
                ? rx + tx
                : null,
        _ => null,
    };

    /// <summary>
    /// 取命中级别的阈值。
    /// </summary>
    private static double? ThresholdOf(AlertRule rule, AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => rule.InfoThreshold,
        AlertSeverity.Warning => rule.WarningThreshold,
        AlertSeverity.Critical => rule.CriticalThreshold,
        _ => null,
    };

    /// <summary>
    /// 构造站内通知的 message key 与参数。
    /// </summary>
    private static (string Key, IReadOnlyDictionary<string, object?> Params) BuildNotificationParams(
        AlertRule rule,
        string? hostName,
        string? containerName,
        double? metricValue,
        double? thresholdValue)
    {
        var host = hostName ?? "-";
        var container = containerName ?? "-";

        return rule.Kind switch
        {
            AlertKind.HostOffline => (
                "notification.alert.hostOffline",
                new Dictionary<string, object?> { ["host"] = host }),

            AlertKind.ContainerCpu => (
                "notification.alert.containerCpu",
                new Dictionary<string, object?>
                {
                    ["host"] = host,
                    ["container"] = container,
                    ["value"] = Round(metricValue, 1),
                    ["threshold"] = Round(thresholdValue, 1),
                }),

            AlertKind.ContainerMemory => (
                "notification.alert.containerMemory",
                new Dictionary<string, object?>
                {
                    ["host"] = host,
                    ["container"] = container,
                    ["value"] = Round(metricValue, 1),
                    ["threshold"] = Round(thresholdValue, 1),
                }),

            AlertKind.ContainerNetwork => (
                "notification.alert.containerNetwork",
                new Dictionary<string, object?>
                {
                    ["host"] = host,
                    ["container"] = container,
                    ["value"] = Round(metricValue / 1024 / 1024, 2),
                    ["threshold"] = Round(thresholdValue / 1024 / 1024, 2),
                }),

            AlertKind.LoginFailureSurge => (
                "notification.alert.loginFailureSurge",
                new Dictionary<string, object?>
                {
                    ["count"] = (long)(metricValue ?? 0),
                    ["window"] = rule.WindowSeconds ?? 300,
                }),

            _ => ("notification.alert.unknown", new Dictionary<string, object?>()),
        };
    }

    /// <summary>
    /// 外部渠道用的纯文本说明。
    /// </summary>
    private static string BuildPlainText(
        AlertRule rule,
        string? hostName,
        string? containerName,
        double? metricValue,
        double? thresholdValue)
    {
        var host = hostName ?? "-";
        var container = containerName ?? "-";

        return rule.Kind switch
        {
            AlertKind.HostOffline => $"主机 {host} 离线",
            AlertKind.ContainerCpu =>
                $"容器 {container}（主机 {host}）CPU 使用率 {Round(metricValue, 1)}%，"
                + $"超过阈值 {Round(thresholdValue, 1)}%",
            AlertKind.ContainerMemory =>
                $"容器 {container}（主机 {host}）内存使用率 {Round(metricValue, 1)}%，"
                + $"超过阈值 {Round(thresholdValue, 1)}%",
            AlertKind.ContainerNetwork =>
                $"容器 {container}（主机 {host}）网络吞吐 {Round(metricValue / 1024 / 1024, 2)} MB/s，"
                + $"超过阈值 {Round(thresholdValue / 1024 / 1024, 2)} MB/s",
            AlertKind.LoginFailureSurge =>
                $"最近 {rule.WindowSeconds ?? 300} 秒登录失败 {metricValue:0} 次",
            _ => "未知告警",
        };
    }

    /// <summary>
    /// 主机目标键。
    /// </summary>
    private static string HostTargetKey(DockerHost host) =>
        $"host:{host.Name.ToLowerInvariant()}";

    /// <summary>
    /// 容器目标键。
    /// </summary>
    private static string ContainerTargetKey(DockerHost host, string containerId) =>
        $"host:{host.Name.ToLowerInvariant()}:container:{containerId}";

    /// <summary>
    /// 计数器键。
    /// </summary>
    private static string CounterKey(AlertRule rule, string targetKey) => $"{rule.Id:N}|{targetKey}";

    /// <summary>
    /// 监控键。
    /// </summary>
    private static string MonitorKey(string hostName, string containerId) =>
        $"{hostName.ToLowerInvariant()}|{containerId}";

    /// <summary>
    /// 四舍五入（空值原样返回）。
    /// </summary>
    private static double? Round(double? value, int digits) =>
        value is null ? null : Math.Round(value.Value, digits, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 一个容器监控项。
    /// </summary>
    private sealed class MonitorEntry
    {
        public required string Key { get; init; }

        public required DockerHost Host { get; init; }

        public required string ContainerId { get; init; }

        public required string ContainerName { get; set; }

        public required IReadOnlyList<AlertRule> Rules { get; set; }

        public CancellationTokenSource Cancellation { get; set; } = new();

        public Task? Task { get; set; }
    }
}
