using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Alerting;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 把编排任务失败落库为站内通知。
/// </summary>
/// <remarks>
/// <para><b>0.11.0 起不再直接推 Hub</b>：通知先落库，再由 <see cref="NotificationDispatcher"/>
/// 按可见性推送。这样用户离开页面后仍能在通知中心看到失败记录。</para>
/// <para><b>为什么不抛异常</b>：通知失败不能让已经失败的任务再失败一次——
/// 失败原因已经写进任务快照与审计日志了。</para>
/// </remarks>
internal sealed class HubStackTaskNotifier : IStackTaskNotifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HubStackTaskNotifier> _logger;

    /// <summary>
    /// 初始化通知器。
    /// </summary>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HubStackTaskNotifier(
        IServiceScopeFactory scopeFactory,
        ILogger<HubStackTaskNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyFailedAsync(
        StackTaskFailureNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            using var scope = _scopeFactory.CreateScope();

            var accessor = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

            var host = await accessor
                .ResolveAsync(notification.HostName, cancellationToken)
                .ConfigureAwait(false);

            await notifications.CreateAsync(
                new NotificationDraft(
                    NotificationSourceKind.StackTaskFailed,
                    AlertSeverity.Critical,
                    "notification.stackFailed",
                    new Dictionary<string, object?>
                    {
                        ["host"] = notification.HostName,
                        ["stack"] = notification.StackName,
                        ["message"] = notification.Message,
                    },
                    host.Id,
                    host.Name,
                    DedupeKey: $"stack:{host.Id:N}:{notification.StackName}:{notification.OccurredAt:O}",
                    notification.OccurredAt),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "记录编排任务失败通知失败（主机={HostName}，编排={StackName}）",
                notification.HostName,
                notification.StackName);
        }
    }
}
