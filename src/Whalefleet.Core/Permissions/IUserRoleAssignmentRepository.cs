namespace Whalefleet.Core.Permissions;

/// <summary>
/// 用户-角色分配仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>继承 <see cref="IRepository{TEntity, TId}"/></b>：与本项目其余仓储同构——
/// CRUD、审计填充与软删除过滤由 <c>Infrastructure/Persistence/RepositoryBase</c> 统一提供。
/// 分配表没有任何理由成为例外；在本接口另写一套签名只会丢掉那三道兜底。</para>
/// <para><b>读取方法默认过滤已撤销（软删除）的分配</b>。</para>
/// <para><b>落地阶段</b>：0.1.0 —— 接口、实体、SQLite 实现与建表在同一迭代交付。</para>
/// </remarks>
public interface IUserRoleAssignmentRepository : IRepository<UserRoleAssignment, Guid>
{
    /// <summary>
    /// 查询某用户在某主机上（<paramref name="hostId"/> 为 <see langword="null"/> 时查全局分配）
    /// 持有的某个角色的分配。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="roleId">角色主键。</param>
    /// <param name="hostId">作用域主机主键；<see langword="null"/> 表示查全局分配。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配记录；不存在或已撤销时返回 <see langword="null"/>。</returns>
    Task<UserRoleAssignment?> GetAsync(
        Guid userId,
        Guid roleId,
        Guid? hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某用户的全部有效分配。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配列表（可能为空）。</returns>
    /// <remarks>
    /// 鉴权路径的热读：有效权限集就是它的角色集合的并集，因此实现须保证
    /// <c>(user_id) WHERE is_deleted = 0</c> 走索引。
    /// </remarks>
    Task<IReadOnlyList<UserRoleAssignment>> ListByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按用户集合批量查分配。
    /// </summary>
    /// <param name="userIds">用户主键集合；为空时直接返回空列表（不发 SQL）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部分配（跨用户）；主键不存在或已软删除的用户不出现。</returns>
    /// <remarks>
    /// 用户列表页要显示「每个人有哪些角色」。逐个 <c>ListByUserAsync</c> 是 N+1；
    /// 而这一页本来就只需要几个角色的名字，一次批量查完在内存里分组即可。
    /// </remarks>
    Task<IReadOnlyList<UserRoleAssignment>> ListByUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某主机上的全部有效分配（主机分配；不含全局分配）。
    /// </summary>
    /// <param name="hostId">主机主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配列表（可能为空）。</returns>
    /// <remarks>供管理界面回答「谁能访问这台机器」；全局分配在该列表里<b>不出现</b>，
    /// 因为它对所有主机生效、无法归属到某一台。</remarks>
    Task<IReadOnlyList<UserRoleAssignment>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计某角色当前的**有效分配**数。
    /// </summary>
    /// <param name="roleId">角色主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配数量。</returns>
    /// <remarks>
    /// 删除角色的前置检查（「这个角色仍在使用」）。只数未撤销的行——
    /// 已撤销的分配不该阻止角色被删掉，否则一次撤销就足以让角色永远删不掉。
    /// </remarks>
    Task<int> CountByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计当前指向某角色的<b>全局分配</b>总数（不含主机分配）。
    /// </summary>
    /// <param name="roleId">角色主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全局分配数量。</returns>
    /// <remarks>
    /// 「至少保留一名管理员」用。主机分配里的 <c>admin</c> 只带来该主机的 Host 域权限，
    /// 不构成系统管理员，因此必须分开计数——否则删用户时会因为某个主机上的 admin 分配而误判。
    /// </remarks>
    Task<int> CountGlobalByRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计某用户当前的**全局分配**中，有多少条指向指定角色。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="roleId">角色主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配数量。</returns>
    /// <remarks>「至少保留一名管理员」检查用；只数全局分配，因为主机分配里的
    /// <c>admin</c> 并不构成系统管理员。</remarks>
    Task<int> CountGlobalByUserAndRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default);
}
