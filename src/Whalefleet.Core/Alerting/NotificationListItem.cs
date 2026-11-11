namespace Whalefleet.Core.Alerting;

/// <summary>
/// 通知列表项（通知 + 当前用户的已读 / 删除状态）。
/// </summary>
/// <param name="Notification">通知本体。</param>
/// <param name="ReadAt">当前用户已读时刻；未读为 <see langword="null"/>。</param>
/// <param name="DismissedAt">当前用户删除时刻；未删除为 <see langword="null"/>。</param>
public sealed record NotificationListItem(
    Notification Notification,
    DateTime? ReadAt,
    DateTime? DismissedAt);
