namespace Whalefleet.Core.Auditing;

/// <summary>
/// 审计日志仓储（<b>只追加 + 只查</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么没有更新与删除</b>：审计的价值建立在「记录不可篡改」上。
/// 接口层面不提供这两个能力，比在实现里加判断更可靠——
/// 调用方连写错的入口都没有。</para>
/// <para><b>为什么没有继承 <c>IRepository&lt;TEntity, TId&gt;</c></b>：那个接口带着
/// <c>RemoveByIdAsync</c> 与全量列表语义，对审计表都是错的。</para>
/// </remarks>
public interface IAuditLogRepository
{
    /// <summary>
    /// 追加一条审计记录。
    /// </summary>
    /// <param name="log">审计记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task AddAsync(AuditLog log, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件分页查询（按发生时间<b>倒序</b>）。
    /// </summary>
    /// <param name="filter">过滤条件。</param>
    /// <param name="offset">跳过的行数。</param>
    /// <param name="limit">本页行数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页记录（可能为空集合，不是 <see langword="null"/>）。</returns>
    Task<IReadOnlyList<AuditLog>> ListAsync(
        AuditLogFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计满足条件的总行数。
    /// </summary>
    /// <param name="filter">过滤条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>总行数。</returns>
    Task<int> CountAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);
}
