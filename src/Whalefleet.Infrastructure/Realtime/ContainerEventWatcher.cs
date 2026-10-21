using System.Collections.Concurrent;
using System.Globalization;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Core.Hosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Infrastructure.Realtime;

/// <summary>
/// 监听各主机的容器事件，把「异常退出」发布到事件总线。
/// </summary>
/// <remarks>
/// <para><b>为什么在 Infrastructure 层</b>：读 Docker 的 events 流是<b>外部资源访问</b>，
/// 按分层判据属于「适配某个具体 SDK」——这一层的事。</para>
/// <para><b>为什么不逐事件查 inspect</b>：拿到一个事件再查一次容器详情，
/// 在容器频繁重启时会把守护进程打爆（每个事件一次 inspect）。
/// 事件里已经带了容器名与退出码，够用了。</para>
/// <para><b>为什么每台主机一条常驻连接</b>：Docker 的 events 流是<b>按守护进程</b>的，
/// 一个客户端只能订阅一个守护进程。因此「监听所有主机」必然是「每台主机一条连接」。
/// 连接断开后要退避重连——守护进程重启是常事，没有退避就会变成
/// 「每秒重连几十次」的自我轰炸。</para>
/// <para><b>为什么周期性重新枚举主机</b>：主机是数据（<c>hosts</c> 表），
/// 管理员随时可能加一台、删一台。周期性枚举让监听列表跟着数据走。</para>
/// </remarks>
internal sealed class ContainerEventWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContainerEventBus _bus;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<ContainerEventWatcher> _logger;

    /// <summary>
    /// 正在监听的宿主（键 = 主机名）。
    /// </summary>
    private readonly ConcurrentDictionary<string, HostListener> _listeners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 初始化事件监听器。
    /// </summary>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="bus">事件总线（发布端）。</param>
    /// <param name="options">实时通道选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerEventWatcher(
        IServiceScopeFactory scopeFactory,
        IContainerEventBus bus,
        IOptions<RealtimeOptions> options,
        ILogger<ContainerEventWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _bus = bus;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.Value.HostEnumerationIntervalSeconds);
        var backoff = TimeSpan.FromSeconds(_options.Value.EventReconnectBackoffSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.SyncListenersAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // 枚举失败不能拖垮整个服务：退避后重试。
                _logger.LogError(exception, "枚举主机以同步容器事件监听失败");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await this.StopAllAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 同步监听列表：新增主机起监听，消失的主机停监听。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task SyncListenersAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(_options.Value.EventReconnectBackoffSeconds);

        List<DockerHost> hosts;

        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IHostRepository>();
            hosts = [.. await repository.QueryAllListAsync(cancellationToken).ConfigureAwait(false)];
        }

        var names = hosts.Select(host => host.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 停掉已消失的主机。
        foreach (var key in _listeners.Keys.ToList())
        {
            if (!names.Contains(key))
            {
                if (_listeners.TryRemove(key, out var removed))
                {
                    await removed.StopAsync().ConfigureAwait(false);

                    _logger.LogInformation("停止监听主机 {HostId} 的容器事件（主机已不存在）", key);
                }
            }
        }

        // 为新增的主机起监听。
        foreach (var host in hosts)
        {
            if (_listeners.ContainsKey(host.Name))
            {
                continue;
            }

            var listener = new HostListener(host.Name, this.ListenAsync);
            _listeners[host.Name] = listener;
            listener.Start();

            _logger.LogInformation("开始监听主机 {HostId} 的容器事件", host.Name);
        }
    }

    /// <summary>
    /// 监听一台主机的容器事件（内部循环：断开 → 退避 → 重连）。
    /// </summary>
    /// <param name="hostId">主机名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task ListenAsync(string hostId, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(_options.Value.EventReconnectBackoffSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var accessor = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();
                var client = await accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

                var parameters = new ContainerEventsParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["type"] = new Dictionary<string, bool> { ["container"] = true },
                        ["event"] = new Dictionary<string, bool>
                        {
                            ["die"] = true,
                            ["oom"] = true,

                            // stop / kill 也必须监听：手动停止同样会产生 die 事件，
                            // 只订 die 的话「手动停了个容器」也会弹一条「异常退出」通知
                            ["stop"] = true,
                            ["kill"] = true,
                        },
                    },
                };

                // ⚠️ 同步收集器：System.Progress<T> 会把回调 Post 到线程池，
                // 那样「流结束」与「最后几个事件被处理完」之间会出现时间窗——事件会丢。
                await client.System
                    .MonitorEventsAsync(parameters, new EventCollector(hostId, _bus, _logger), cancellationToken)
                    .ConfigureAwait(false);

                // 流正常结束（守护进程关闭了连接）→ 退避后重连。
                _logger.LogWarning("主机 {HostId} 的事件流已结束，{Seconds} 秒后重连", hostId, backoff.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "主机 {HostId} 的事件流异常，{Seconds} 秒后重连",
                    hostId,
                    backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 停止全部监听。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    private async Task StopAllAsync()
    {
        foreach (var key in _listeners.Keys.ToList())
        {
            if (_listeners.TryRemove(key, out var listener))
            {
                await listener.StopAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 一台主机的监听句柄。
    /// </summary>
    /// <param name="HostId">主机名。</param>
    /// <param name="Runner">实际监听逻辑。</param>
    private sealed class HostListener(string hostId, Func<string, CancellationToken, Task> runner) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        /// <summary>
        /// 启动监听。
        /// </summary>
        public void Start() => _ = Task.Run(() => runner(hostId, _cancellation.Token));

        /// <summary>
        /// 停止监听。
        /// </summary>
        /// <returns>表示异步操作的任务。</returns>
        public async Task StopAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }

    /// <summary>
    /// Docker 事件的同步收集器。
    /// </summary>
    /// <remarks>
    /// <b>必须是同步的</b>：<c>System.Progress&lt;T&gt;</c> 会把回调 Post 到线程池，
    /// 于是「流结束」与「最后几个事件被处理完」之间出现时间窗——事件会丢。
    /// SDK 在一条读取循环里顺序回调，因此这里不需要加锁。
    /// </remarks>
    private sealed class EventCollector : IProgress<Message>
    {
        private readonly string _hostId;
        private readonly IContainerEventBus _bus;
        private readonly ILogger _logger;

        /// <summary>
        /// 「刚被主动停掉」的容器（容器 ID → 收到 stop/kill 的时刻）。
        /// </summary>
        /// <remarks>
        /// Docker 的手动停止路径是 <c>stop</c> → <c>die</c>（<c>kill</c> 同理），
        /// 而 <c>die</c> 本身<b>不说明原因</b>——它同样会为「容器自己崩了」发出。
        /// 因此只能先记住「刚刚有人叫停」，等 <c>die</c> 到来时把它当作正常停止丢掉。
        /// </remarks>
        private readonly Dictionary<string, DateTimeOffset> _recentlyStopped = new(StringComparer.Ordinal);

        /// <summary>
        /// 刚刚已经发过通知的容器（容器 ID → 发布时刻），用于 <c>oom</c> + <c>die</c> 去重。
        /// </summary>
        /// <remarks>
        /// 一次 OOM 死亡会连着发两条事件（<c>oom</c> 与 <c>die</c>）。不去重的话，
        /// 用户会在铃铛里看到同一次事故的两条通知，且其中一条没有 OOM 标记。
        /// </remarks>
        private readonly Dictionary<string, DateTimeOffset> _recentlyPublished = new(StringComparer.Ordinal);

        /// <summary>
        /// 「主动停止」的判定窗口。
        /// </summary>
        /// <remarks>
        /// 10 秒的依据：<c>stop</c> 到 <c>die</c> 在本地守护进程上通常只有几毫秒（先 SIGTERM、
        /// 超时后再 SIGKILL，默认 10 秒）。窗口取 10 秒正好覆盖默认超时，
        /// 又短到不会把「停完之后一分钟自己又崩了」误判成手动停止。
        /// </remarks>
        private static readonly TimeSpan StopWindow = TimeSpan.FromSeconds(10);

        /// <summary>
        /// <c>oom</c> 与 <c>die</c> 的去重窗口（同一次死亡的两次事件）。
        /// </summary>
        private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 两个字典的修剪阈值：超过这个条目数时顺手清掉过期项。
        /// </summary>
        /// <remarks>
        /// 不修剪的话，一台跑过几千个容器的机器会让这两个字典只增不减——
        /// 而它们只是「最近几秒」的辅助状态。
        /// </remarks>
        private const int PruneThreshold = 64;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="hostId">主机名。</param>
        /// <param name="bus">事件总线。</param>
        /// <param name="logger">日志。</param>
        public EventCollector(string hostId, IContainerEventBus bus, ILogger logger)
        {
            _hostId = hostId;
            _bus = bus;
            _logger = logger;
        }

        /// <inheritdoc />
        public void Report(Message value)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!string.Equals(value.Type, "container", StringComparison.Ordinal))
            {
                return;
            }

            var action = value.Action;

            if (action is not ("die" or "oom" or "stop" or "kill"))
            {
                return;
            }

            var actor = value.Actor;
            var containerId = actor?.ID ?? string.Empty;

            if (string.IsNullOrWhiteSpace(containerId))
            {
                return;
            }

            var occurredAt = value.Time is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.UtcNow;

            this.Prune(occurredAt);

            // 主动停止：只记下来，不通知。紧随其后的 die 会被这条记录吞掉。
            if (action is "stop" or "kill")
            {
                _recentlyStopped[containerId] = occurredAt;
                return;
            }

            if (action == "die")
            {
                if (_recentlyStopped.TryGetValue(containerId, out var stoppedAt)
                    && occurredAt - stoppedAt <= StopWindow)
                {
                    _recentlyStopped.Remove(containerId);

                    _logger.LogDebug(
                        "容器 {ContainerId} 为主动停止，不发通知（主机={HostId}）",
                        containerId,
                        _hostId);

                    return;
                }

                // 同一次死亡的 oom 事件已经发过了
                if (_recentlyPublished.TryGetValue(containerId, out var publishedAt)
                    && occurredAt - publishedAt <= DedupeWindow)
                {
                    return;
                }
            }

            // 容器名取自事件属性：那是 Docker 顺手带上的，不必为每个事件再查一次 inspect。
            var name = actor?.Attributes is { } attributes
                && attributes.TryGetValue("name", out var attributeName)
                    ? attributeName
                    : string.Empty;

            var exitCode = ParseExitCode(actor?.Attributes);
            var outOfMemory = string.Equals(action, "oom", StringComparison.Ordinal);

            _recentlyPublished[containerId] = occurredAt;

            var notification = new ContainerEventNotification(
                _hostId,
                containerId,
                name,
                exitCode,
                outOfMemory,
                occurredAt);

            _logger.LogInformation(
                "容器异常退出（主机={HostId}，容器={ContainerName}，退出码={ExitCode}，OOM={OutOfMemory}）",
                _hostId,
                name,
                exitCode,
                outOfMemory);

            // 发布是 fire-and-forget：总线满了也只是丢事件，不该拖住 Docker 的读取回调。
            _ = _bus.PublishAsync(notification).AsTask();
        }

        /// <summary>
        /// 清掉两个辅助字典里的过期项。
        /// </summary>
        /// <param name="now">当前事件时刻。</param>
        /// <returns>无。</returns>
        private void Prune(DateTimeOffset now)
        {
            if (_recentlyStopped.Count < PruneThreshold && _recentlyPublished.Count < PruneThreshold)
            {
                return;
            }

            PruneOlderThan(_recentlyStopped, now, StopWindow);
            PruneOlderThan(_recentlyPublished, now, DedupeWindow);
        }

        private static void PruneOlderThan(
            Dictionary<string, DateTimeOffset> entries,
            DateTimeOffset now,
            TimeSpan window)
        {
            foreach (var (key, at) in entries.ToArray())
            {
                if (now - at > window)
                {
                    entries.Remove(key);
                }
            }
        }

        /// <summary>
        /// 从事件属性里取退出码。
        /// </summary>
        /// <param name="attributes">事件属性。</param>
        /// <returns>退出码；取不到时为 0。</returns>
        private static long ParseExitCode(IDictionary<string, string>? attributes)
        {
            if (attributes is null
                || !attributes.TryGetValue("exitCode", out var raw)
                || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
            {
                return 0;
            }

            return exitCode;
        }
    }
}
