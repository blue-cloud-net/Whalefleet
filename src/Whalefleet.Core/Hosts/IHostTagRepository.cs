namespace Whalefleet.Core.Hosts;

/// <summary>
/// 主机标签仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 与全量列表见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>QueryByIdAsync</c> / <c>QueryAllListAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> /
/// <c>RemoveByIdAsync</c> 由基接口统一声明。</para>
/// <para><b>位置</b>：接口在 <c>Core</c>，实现在 <c>Infrastructure/Persistence</c>。</para>
/// <para><b>关联关系是本接口的一部分</b>：打标 / 清标是「关系」而不是标签实体自己的状态，
/// 但仍放在这里——否则应用层要同时依赖两个仓储才能完成一次「打标」，而它们必然在同一个事务边界里。</para>
/// <para><b>幂等由存储层保证</b>：<see cref="AssignAsync"/> 与 <see cref="RevokeAsync"/> 重复调用不报错。
/// 前端允许「全选 + 批量打标」这种必然包含重复的操作，非幂等会让第二次点击变成失败。</para>
/// </remarks>
public interface IHostTagRepository : Common.IRepository<HostTag, Guid>
{
    /// <summary>
    /// 按标签名查询（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="name">标签名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<HostTag?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断标签名是否已被占用（不区分大小写，忽略已软删除的标签）。
    /// </summary>
    /// <param name="name">标签名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量查询指定主机身上的标签。
    /// </summary>
    /// <param name="hostIds">主机主键集合（<c>hosts.id</c>，不是主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机主键 → 标签列表（按标签名排序）；没有任何标签的主机不出现在字典里。</returns>
    /// <remarks>
    /// <b>一次查询</b>而不是逐台查：主机列表要显示每台主机的标签，逐台查在 20 台主机时
    /// 就是 20 次往返，而这个接口恰好会被总览页反复调用。
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<HostTag>>> QueryByHostIdsAsync(
        IReadOnlyCollection<Guid> hostIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计每个标签挂了多少台主机。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签主键 → 主机数；没有关联的标签不出现在字典里（调用方按 0 处理）。</returns>
    Task<IReadOnlyDictionary<Guid, int>> CountByTagAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 给若干台主机打若干标签（幂等）。
    /// </summary>
    /// <param name="hostIds">主机主键集合。</param>
    /// <param name="tagIds">标签主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>已存在的关联被忽略，不报错也不刷新创建时间。</remarks>
    Task AssignAsync(
        IReadOnlyCollection<Guid> hostIds,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销若干台主机身上的若干标签（幂等）。
    /// </summary>
    /// <param name="hostIds">主机主键集合。</param>
    /// <param name="tagIds">标签主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>不存在的关联被忽略。</remarks>
    Task RevokeAsync(
        IReadOnlyCollection<Guid> hostIds,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除某个标签的全部关联（删除标签前调用）。
    /// </summary>
    /// <param name="tagId">标签主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// 删除标签<b>不</b>删主机：标签只是标记。但关联行必须一并清除，
    /// 否则外键会挡住标签的物理删除路径，也会让「同名标签重建」继承旧的关联。
    /// </remarks>
    Task RemoveAssignmentsByTagAsync(Guid tagId, CancellationToken cancellationToken = default);
}
