using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 把落库后的站内通知推给<b>有权看见该主机</b>的在线连接。
/// </summary>
/// <remarks>
/// <para><b>为什么逐连接判权限</b>：通知 Hub 是全局的，而「谁能看见某台主机」取决于
/// 该用户的主机级角色分配，且权限运行时可变。按主机分组做不到这一点。</para>
/// <para><b>为什么订阅总线而不是被直接调用</b>：通知来源有告警评估器、容器事件、编排任务，
/// 它们都在应用层 / 基础设施层。让它们直接依赖 Hub 会把依赖方向反过来。</para>
/// </remarks>
internal sealed class NotificationDispatcher : BackgroundService
{
    private readonly INotificationBus _bus;
    private readonly RealtimeConnectionRegistry _connections;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDispatcher> _logger;

    /// <summary>
    /// 初始化派发器。
    /// </summary>
    public NotificationDispatcher(
        INotificationBus bus,
        RealtimeConnectionRegistry connections,
        IHubContext<NotificationHub> hubContext,
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDispatcher> logger)
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
                    _logger.LogError(exception, "站内通知派发失败，继续处理后续事件");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "站内通知派发循环异常退出");
        }
    }

    /// <summary>
    /// 把一条通知推给有权看见该主机的连接。
    /// </summary>
    private async Task DispatchAsync(Notification notification, CancellationToken cancellationToken)
    {
        var targets = _connections.Snapshot();

        if (targets.Count == 0)
        {
            return;
        }

        var payload = new NotificationPayload(
            notification.Id,
            NotificationSourceKindText.ToText(notification.SourceKind),
            AlertSeverityText.ToText(notification.Severity),
            notification.MessageKey,
            notification.ParamsJson,
            notification.HostId,
            notification.HostName,
            notification.OccurredAt);

        if (notification.HostId is not { } hostId)
        {
            foreach (var (connectionId, _) in targets)
            {
                await this.SendAsync(connectionId, payload, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

        foreach (var (connectionId, userId) in targets)
        {
            var allowed = await permissions
                .HasPermissionAsync(userId, hostId, Permission.HostRead, cancellationToken)
                .ConfigureAwait(false);

            if (allowed)
            {
                using var document = System.Text.Json.JsonDocument.Parse(notification.ParamsJson);
                var checker = scope.ServiceProvider.GetRequiredService<IContainerPermissionChecker>();
                if (document.RootElement.TryGetProperty("containerId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var effective = await checker.GetPermissionsAsync(userId, hostId, id.GetString()!, cancellationToken).ConfigureAwait(false);
                    allowed = effective.Contains(Permission.ContainerRead);
                }
                else if (notification.SourceKind == NotificationSourceKind.ContainerDied || notification.MessageKey.StartsWith("notification.alert.container", StringComparison.Ordinal))
                {
                    allowed = !await checker.IsRestrictedAsync(userId, hostId, cancellationToken).ConfigureAwait(false);
                }
            }
            if (allowed)
            {
                await this.SendAsync(connectionId, payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 推给单个连接（失败只记 Debug：客户端可能已断开）。
    /// </summary>
    private async Task SendAsync(
        string connectionId,
        NotificationPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hubContext.Clients
                .Client(connectionId)
                .SendAsync("OnNotification", payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "推送站内通知失败（连接={ConnectionId}）：{Reason}",
                connectionId,
                exception.Message);
        }
    }
}
