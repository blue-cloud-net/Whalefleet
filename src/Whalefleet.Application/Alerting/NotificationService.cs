using System.Text.Json;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 站内通知用例（列表 / 已读 / 删除 / 创建）。
/// </summary>
/// <remarks>
/// <para><b>可见性在这里统一收口</b>：列表、未读数、已读、删除都走同一个
/// <see cref="NotificationFilter"/>，避免「列表里看不到、红点却数得到」这种自相矛盾。</para>
/// <para><b>创建是内部入口</b>：告警评估器与现有实时通知都通过它落库并发布总线，
/// 不直接碰仓储，保证「落库 + 推实时」永远成对发生。</para>
/// </remarks>
public sealed class NotificationService : INotificationWriter
{
    private const int _defaultPageSize = 20;
    private const int _maxPageSize = 100;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INotificationRepository _notifications;
    private readonly INotificationBus _bus;
    private readonly IPermissionChecker _permissions;
    private readonly IHostRepository _hosts;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly IContainerPermissionChecker _containerPermissions;
    private readonly IUserRoleAssignmentRepository _assignments;
    private readonly IAuthorizationSettingsStore _settings;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    public NotificationService(
        INotificationRepository notifications,
        INotificationBus bus,
        IPermissionChecker permissions,
        IHostRepository hosts,
        ICurrentUser currentUser,
        TimeProvider clock, IContainerPermissionChecker containerPermissions, IUserRoleAssignmentRepository assignments, IAuthorizationSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(clock);

        _notifications = notifications;
        _bus = bus;
        _permissions = permissions;
        _hosts = hosts;
        _currentUser = currentUser;
        _clock = clock;
        _containerPermissions = containerPermissions;
        _assignments = assignments;
        _settings = settings;
    }

    /// <summary>
    /// 创建一条通知并发布实时事件。
    /// </summary>
    public async Task<Notification> CreateAsync(
        NotificationDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var paramsJson = JsonSerializer.Serialize(draft.Params, _jsonOptions);

        var notification = Notification.Create(
            Guid.NewGuid(),
            draft.SourceKind,
            draft.Severity,
            draft.MessageKey,
            paramsJson,
            draft.HostId,
            draft.HostName,
            draft.DedupeKey,
            draft.OccurredAt);

        await _notifications.AddAsync(notification, cancellationToken).ConfigureAwait(false);
        await _bus.PublishAsync(notification, cancellationToken).ConfigureAwait(false);

        return notification;
    }

    /// <summary>
    /// 分页列出当前用户可见的通知。
    /// </summary>
    public async Task<NotificationListResult> ListAsync(
        ListNotificationsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = this.RequireUserId();

        var filter = await this.BuildFilterAsync(
            userId,
            request.UnreadOnly,
            request.Severity,
            request.SourceKind,
            request.Search,
            cancellationToken).ConfigureAwait(false);

        var pageIndex = Math.Max(request.PageIndex ?? 0, 0);
        var pageSize = Math.Clamp(request.PageSize ?? _defaultPageSize, 1, _maxPageSize);

        var items = await _notifications.ListAsync(
            userId,
            filter,
            pageIndex * pageSize,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        var total = await _notifications.CountAsync(userId, filter, cancellationToken).ConfigureAwait(false);
        var unread = await _notifications.CountUnreadAsync(userId, filter, cancellationToken)
            .ConfigureAwait(false);

        return new NotificationListResult(
            [.. items.Select(ToDto)],
            total,
            unread,
            pageIndex,
            pageSize);
    }

    /// <summary>
    /// 标记已读。
    /// </summary>
    public async Task<int> MarkReadAsync(
        MarkNotificationsReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = this.RequireUserId();
        var filter = await this.BuildFilterAsync(userId, null, null, null, null, cancellationToken)
            .ConfigureAwait(false);

        var ids = request.All ? null : (request.Ids ?? []);

        return await _notifications.MarkReadAsync(
            userId,
            ids,
            _clock.GetUtcNow().UtcDateTime,
            filter,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除（对本人隐藏）通知。
    /// </summary>
    public async Task<int> DismissAsync(
        RemoveNotificationsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = this.RequireUserId();
        var filter = await this.BuildFilterAsync(userId, null, null, null, null, cancellationToken)
            .ConfigureAwait(false);

        var ids = request.All ? null : (request.Ids ?? []);

        return await _notifications.DismissAsync(
            userId,
            ids,
            _clock.GetUtcNow().UtcDateTime,
            filter,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造可见性 + 筛选项。
    /// </summary>
    private async Task<NotificationFilter> BuildFilterAsync(
        Guid userId,
        bool? unreadOnly,
        string? severity,
        string? sourceKind,
        string? search,
        CancellationToken cancellationToken)
    {
        var includeAllHosts = await _permissions.IsAdministratorAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<Guid> visibleHostIds = [];

        if (!includeAllHosts)
        {
            var hosts = await _hosts.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
            var visible = await _permissions.FilterVisibleHostsAsync(userId, hosts, cancellationToken)
                .ConfigureAwait(false);

            visibleHostIds = [.. visible.Select(host => host.Id)];
        }

        var containerAccess = new Dictionary<Guid, ContainerNotificationAccess>();
        if (!includeAllHosts)
        {
            var rows = await _assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);
            var strict = await _settings.GetModeAsync(cancellationToken).ConfigureAwait(false) == "requireAssignment";
            foreach (var hostId in visibleHostIds)
            {
                var selected = new List<string>();
                foreach (var id in rows.Where(a => a.HostId == hostId && a.ContainerId is not null).Select(a => a.ContainerId!).Distinct(StringComparer.Ordinal))
                {
                    var effective = await _containerPermissions.GetPermissionsAsync(userId, hostId, id, cancellationToken).ConfigureAwait(false);
                    if (effective.Contains(Permission.ContainerRead) == strict) selected.Add(id);
                }
                var canReadContainers = await _permissions.HasPermissionAsync(userId, hostId, Permission.ContainerRead, cancellationToken).ConfigureAwait(false);
                if (!canReadContainers) { containerAccess[hostId] = new ContainerNotificationAccess(true, []); continue; }
                if (strict || selected.Count > 0) containerAccess[hostId] = new ContainerNotificationAccess(strict, selected);
            }
        }
        return new NotificationFilter
        {
            ContainerAccess = containerAccess,
            IncludeAllHosts = includeAllHosts,
            VisibleHostIds = visibleHostIds,
            UnreadOnly = unreadOnly,
            Severity = ParseSeverity(severity),
            SourceKind = ParseSourceKind(sourceKind),
            Search = String.IsNullOrWhiteSpace(search) ? null : search.Trim(),
        };
    }

    /// <summary>
    /// 解析严重度筛选值。
    /// </summary>
    private static AlertSeverity? ParseSeverity(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return AlertSeverityText.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"不认识的严重度「{value}」，可选 info / warning / critical。",
                exception);
        }
    }

    /// <summary>
    /// 解析来源类型筛选值。
    /// </summary>
    private static NotificationSourceKind? ParseSourceKind(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return NotificationSourceKindText.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"不认识的通知来源「{value}」。",
                exception);
        }
    }

    /// <summary>
    /// 取当前登录用户；未登录抛 <c>1100</c>。
    /// </summary>
    private Guid RequireUserId() =>
        _currentUser.UserId
        ?? throw BusinessException.Unauthenticated("未认证");

    /// <summary>
    /// 实体 + 用户状态 → DTO。
    /// </summary>
    private static NotificationDto ToDto(NotificationListItem item) => new(
        item.Notification.Id,
        NotificationSourceKindText.ToText(item.Notification.SourceKind),
        AlertSeverityText.ToText(item.Notification.Severity),
        item.Notification.MessageKey,
        DeserializeParams(item.Notification.ParamsJson),
        item.Notification.HostId,
        item.Notification.HostName,
        item.ReadAt is not null,
        item.ReadAt,
        item.Notification.OccurredAt);

    /// <summary>
    /// 反序列化参数 JSON。
    /// </summary>
    private static Dictionary<string, object?> DeserializeParams(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _jsonOptions);

            if (raw is null)
            {
                return new Dictionary<string, object?>();
            }

            return raw.ToDictionary(
                pair => pair.Key,
                pair => ToObject(pair.Value),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// 把 <see cref="JsonElement"/> 还原成可序列化的基础类型。
    /// </summary>
    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };
}
