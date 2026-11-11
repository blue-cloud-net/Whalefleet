namespace Whalefleet.Core.Alerting;

/// <summary>
/// 通知渠道仓储（持久化契约）。
/// </summary>
public interface INotificationChannelRepository : Common.IRepository<NotificationChannel, Guid>
{
    /// <summary>
    /// 判断渠道名是否已被占用（不区分大小写，忽略已软删除）。
    /// </summary>
    /// <param name="name">渠道名。</param>
    /// <param name="excludeId">更新时排除自身；新建传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(
        string name,
        Guid? excludeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键集合批量查询。
    /// </summary>
    /// <param name="ids">渠道主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>查到的渠道；不存在的静默跳过。</returns>
    Task<IReadOnlyList<NotificationChannel>> QueryByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);
}
