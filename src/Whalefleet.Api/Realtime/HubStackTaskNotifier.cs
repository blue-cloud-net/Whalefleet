using Microsoft.AspNetCore.SignalR;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Core.Permissions;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 把编排任务失败推给<b>有权看见该主机</b>的在线连接。
/// </summary>
/// <remarks>
/// <para><b>与容器事件派发同一口径</b>：通知 Hub 是全局的（路径里没有 <c>hostId</c>），
/// 而「谁能看见某台主机」取决于该用户的主机级角色分配，且权限是运行时可变的。
/// 因此逐连接判 <c>host.read</c>，无权的一律不推。</para>
/// <para><b>为什么在 Api 层</b>：「推给浏览器」是入站适配器的事，
/// 应用层只表达「这件事发生了，该通知」这一业务意图。</para>
/// <para><b>为什么不抛异常</b>：通知失败不能让已经失败的任务再失败一次——
/// 失败原因已经写进任务快照与审计日志了，推不出去只是「用户没收到铃铛」。</para>
/// </remarks>
internal sealed class HubStackTaskNotifier : IStackTaskNotifier
{
    private readonly RealtimeConnectionRegistry _connections;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HubStackTaskNotifier> _logger;

    /// <summary>
    /// 初始化通知器。
    /// </summary>
    /// <param name="connections">连接注册表。</param>
    /// <param name="hubContext">通知 Hub 上下文。</param>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HubStackTaskNotifier(
        RealtimeConnectionRegistry connections,
        IHubContext<NotificationHub> hubContext,
        IServiceScopeFactory scopeFactory,
        ILogger<HubStackTaskNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _hubContext = hubContext;
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
            var targets = _connections.Snapshot();

            if (targets.Count == 0)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();

            var hosts = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();
            var permissions = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

            var host = await hosts.ResolveAsync(notification.HostName, cancellationToken).ConfigureAwait(false);

            foreach (var (connectionId, userId) in targets)
            {
                var allowed = await permissions
                    .HasPermissionAsync(userId, host.Id, Permission.HostRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!allowed)
                {
                    continue;
                }

                try
                {
                    await _hubContext.Clients
                        .Client(connectionId)
                        .SendAsync("OnStackTaskFailed", notification, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 客户端已断开：推不出去而已，不影响其它连接。
                    _logger.LogDebug(
                        "推送编排任务失败通知失败（连接={ConnectionId}）：{Reason}",
                        connectionId,
                        exception.Message);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "推送编排任务失败通知失败（主机={HostName}，编排={StackName}）",
                notification.HostName,
                notification.StackName);
        }
    }
}
