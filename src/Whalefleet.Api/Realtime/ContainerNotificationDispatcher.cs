using Microsoft.AspNetCore.SignalR;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Core.Permissions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 把容器事件推给<b>有权看见该主机</b>的在线连接。
/// </summary>
/// <remarks>
/// <para><b>为什么逐连接判权限</b>：通知 Hub 是<b>全局</b>的（路径里没有 <c>hostId</c>），
/// 而「谁能看见某台主机」取决于该用户的<b>主机级角色分配</b>，
/// 且权限是<b>运行时可变</b>的（角色改完即时生效）。
/// 按主机分组（<c>Clients.Group</c>）做不到这一点——分组在连接建立时就固定了。</para>
/// <para><b>为什么在 Api 层</b>：「推给浏览器」是入站适配器的事；
/// 事件的<b>来源</b>在 Infrastructure，两者之间由应用层的事件总线解耦，
/// 依赖方向不反转。</para>
/// </remarks>
internal sealed class ContainerNotificationDispatcher : BackgroundService
{
    private readonly IContainerEventBus _bus;
    private readonly RealtimeConnectionRegistry _connections;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContainerNotificationDispatcher> _logger;

    /// <summary>
    /// 初始化派发器。
    /// </summary>
    /// <param name="bus">事件总线（订阅端）。</param>
    /// <param name="connections">连接注册表。</param>
    /// <param name="hubContext">通知 Hub 上下文。</param>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerNotificationDispatcher(
        IContainerEventBus bus,
        RealtimeConnectionRegistry connections,
        IHubContext<NotificationHub> hubContext,
        IServiceScopeFactory scopeFactory,
        ILogger<ContainerNotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _bus = bus;
        _connections = connections;
        _hubContext = hubContext;
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
                await this.DispatchAsync(notification, stoppingToken).ConfigureAwait(false);
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
    /// 把一条事件推给有权看见该主机的连接。
    /// </summary>
    /// <param name="notification">事件内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task DispatchAsync(
        ContainerEventNotification notification,
        CancellationToken cancellationToken)
    {
        var targets = _connections.Snapshot();

        if (targets.Count == 0)
        {
            return;
        }

        // 主机 → 可见用户集合：同一台主机上多个用户时只查一次权限。
        var visibleUsers = await this.ResolveVisibleUsersAsync(notification.HostId, cancellationToken)
            .ConfigureAwait(false);

        if (visibleUsers.Count == 0)
        {
            return;
        }

        foreach (var (connectionId, userId) in targets)
        {
            if (!visibleUsers.Contains(userId))
            {
                continue;
            }

            try
            {
                await _hubContext.Clients
                    .Client(connectionId)
                    .SendAsync("OnContainerDied", notification, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 客户端已断开：推不出去而已，不影响其它连接。
                _logger.LogDebug(
                    "推送容器事件失败（连接={ConnectionId}）：{Reason}",
                    connectionId,
                    exception.Message);
            }
        }
    }

    /// <summary>
    /// 解析对某台主机有可见权限的用户集合。
    /// </summary>
    /// <param name="hostId">主机名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户主键集合。</returns>
    /// <remarks>
    /// 判据是 <c>host.read</c>——与「能不能在页面上进入这台主机」同一口径。
    /// 更细的 <c>container.read</c> 归 0.12.0 的资源级授权一并收口。
    /// </remarks>
    private async Task<IReadOnlySet<Guid>> ResolveVisibleUsersAsync(
        string hostId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var hosts = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

        var host = await hosts.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        var visible = new HashSet<Guid>();

        foreach (var (connectionId, userId) in _connections.Snapshot())
        {
            var allowed = await permissions
                .HasPermissionAsync(userId, host.Id, Permission.HostRead, cancellationToken)
                .ConfigureAwait(false);

            if (allowed)
            {
                visible.Add(userId);
            }
        }

        return visible;
    }
}
