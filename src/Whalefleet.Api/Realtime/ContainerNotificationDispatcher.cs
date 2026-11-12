using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Alerting;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 把容器异常退出事件落库为站内通知。
/// </summary>
/// <remarks>
/// <para><b>0.11.0 起不再直接推 Hub</b>：通知先落库，再由 <see cref="NotificationDispatcher"/>
/// 按可见性推给在线连接。这样铃铛的历史与实时是同一份数据，且「用户离线时错过的事件」
/// 也能在通知中心补看。</para>
/// <para><b>为什么在 Api 层</b>：它是「入站适配器」的一部分——把内部事件翻译成用户可见的通知。
/// 事件的来源在 Infrastructure，两者之间由应用层的事件总线解耦。</para>
/// </remarks>
internal sealed class ContainerNotificationDispatcher : BackgroundService
{
    private readonly IContainerEventBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContainerNotificationDispatcher> _logger;

    /// <summary>
    /// 初始化派发器。
    /// </summary>
    /// <param name="bus">事件总线（订阅端）。</param>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerNotificationDispatcher(
        IContainerEventBus bus,
        IServiceScopeFactory scopeFactory,
        ILogger<ContainerNotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _bus = bus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in _bus.SubscribeAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await this.DispatchAsync(notification, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "容器事件派发失败，继续处理后续事件。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "容器事件派发循环异常退出");
        }
    }

    /// <summary>
    /// 把一条容器事件落库为站内通知。
    /// </summary>
    /// <param name="notification">事件内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task DispatchAsync(
        ContainerEventNotification notification,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        var host = await accessor.ResolveAsync(notification.HostId, cancellationToken).ConfigureAwait(false);

        var name = String.IsNullOrWhiteSpace(notification.ContainerName)
            ? notification.ContainerId[..Math.Min(12, notification.ContainerId.Length)]
            : notification.ContainerName;

        var (messageKey, parameters, severity) = BuildNotification(notification, name);
        var occurredAt = notification.OccurredAt.UtcDateTime;
        parameters = new Dictionary<string, object?>(parameters) { ["containerId"] = notification.ContainerId };

        await notifications.CreateAsync(
            new NotificationDraft(
                NotificationSourceKind.ContainerDied,
                severity,
                messageKey,
                parameters,
                host.Id,
                host.Name,
                DedupeKey: $"container:{host.Id:N}:{notification.ContainerId}:{occurredAt:O}",
                occurredAt),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把容器事件翻译成 i18n key + 参数 + 严重度。
    /// </summary>
    private static (string Key, IReadOnlyDictionary<string, object?> Params, AlertSeverity Severity)
        BuildNotification(ContainerEventNotification notification, string name)
    {
        if (notification.OutOfMemory)
        {
            return (
                "notification.oom",
                new Dictionary<string, object?> { ["name"] = name, ["code"] = notification.ExitCode },
                AlertSeverity.Critical);
        }

        if (notification.ExitCode == 0)
        {
            return (
                "notification.exited",
                new Dictionary<string, object?> { ["name"] = name },
                AlertSeverity.Warning);
        }

        return (
            "notification.died",
            new Dictionary<string, object?> { ["name"] = name, ["code"] = notification.ExitCode },
            AlertSeverity.Warning);
    }
}
