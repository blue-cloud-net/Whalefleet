namespace Whalefleet.Core.Alerting;

/// <summary>
/// 站内通知仓储（只增 + 查询 + 每用户状态）。
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// 追加一条通知。
    /// </summary>
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键查询通知。
    /// </summary>
    Task<Notification?> QueryByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页查询当前用户可见的通知（按发生时间倒序）。
    /// </summary>
    Task<IReadOnlyList<NotificationListItem>> ListAsync(
        Guid userId,
        NotificationFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计当前用户可见的通知总数。
    /// </summary>
    Task<int> CountAsync(
        Guid userId,
        NotificationFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计当前用户可见且未读的通知数（不含已删除）。
    /// </summary>
    Task<int> CountUnreadAsync(
        Guid userId,
        NotificationFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记已读。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="ids">通知主键集合；<see langword="null"/> 表示全部可见未读。</param>
    /// <param name="readAt">已读时刻（UTC）。</param>
    /// <param name="filter">可见性条件（<paramref name="ids"/> 为 null 时用于限制范围）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>影响行数。</returns>
    Task<int> MarkReadAsync(
        Guid userId,
        IReadOnlyCollection<Guid>? ids,
        DateTime readAt,
        NotificationFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除（对本人隐藏）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="ids">通知主键集合；<see langword="null"/> 表示全部可见通知。</param>
    /// <param name="dismissedAt">删除时刻（UTC）。</param>
    /// <param name="filter">可见性条件（<paramref name="ids"/> 为 null 时用于限制范围）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>影响行数。</returns>
    Task<int> DismissAsync(
        Guid userId,
        IReadOnlyCollection<Guid>? ids,
        DateTime dismissedAt,
        NotificationFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除早于指定时刻的通知及其用户状态（保留期裁剪）。
    /// </summary>
    Task<int> TrimAsync(DateTime beforeUtc, CancellationToken cancellationToken = default);
}
