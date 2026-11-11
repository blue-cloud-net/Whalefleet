namespace Whalefleet.Application.Alerting;

/// <summary>
/// 通知对外投影。
/// </summary>
/// <param name="Id">通知主键。</param>
/// <param name="SourceKind">来源类型（kebab-case）。</param>
/// <param name="Severity">严重度（小写文本）。</param>
/// <param name="MessageKey">i18n key。</param>
/// <param name="Params">插值参数。</param>
/// <param name="HostId">主机主键；全局通知为 <see langword="null"/>。</param>
/// <param name="HostName">主机名快照。</param>
/// <param name="Read">当前用户是否已读。</param>
/// <param name="ReadAt">已读时刻（UTC）；未读为 <see langword="null"/>。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record NotificationDto(
    Guid Id,
    string SourceKind,
    string Severity,
    string MessageKey,
    IReadOnlyDictionary<string, object?> Params,
    Guid? HostId,
    string? HostName,
    bool Read,
    DateTime? ReadAt,
    DateTime OccurredAt);

/// <summary>
/// 通知分页结果。
/// </summary>
public sealed record NotificationListResult(
    IReadOnlyList<NotificationDto> Items,
    int Total,
    int Unread,
    int PageIndex,
    int PageSize);

/// <summary>
/// 通知列表请求。
/// </summary>
public sealed record ListNotificationsRequest(
    bool? UnreadOnly,
    string? Severity,
    string? SourceKind,
    string? Search,
    int? PageIndex,
    int? PageSize);

/// <summary>
/// 标记已读的请求。
/// </summary>
/// <param name="Ids">通知主键集合；<paramref name="All"/> 为 <see langword="true"/> 时忽略。</param>
/// <param name="All">是否把全部可见未读标记为已读。</param>
public sealed record MarkNotificationsReadRequest(IReadOnlyList<Guid>? Ids, bool All);

/// <summary>
/// 删除（对本人隐藏）通知的请求。
/// </summary>
/// <param name="Ids">通知主键集合；<paramref name="All"/> 为 <see langword="true"/> 时忽略。</param>
/// <param name="All">是否删除全部可见通知。</param>
public sealed record RemoveNotificationsRequest(IReadOnlyList<Guid>? Ids, bool All);

/// <summary>
/// 通知的创建草稿（供告警评估器与现有实时通知落库使用）。
/// </summary>
public sealed record NotificationDraft(
    Core.Alerting.NotificationSourceKind SourceKind,
    Core.Alerting.AlertSeverity Severity,
    string MessageKey,
    IReadOnlyDictionary<string, object?> Params,
    Guid? HostId,
    string? HostName,
    string? DedupeKey,
    DateTime OccurredAt);
