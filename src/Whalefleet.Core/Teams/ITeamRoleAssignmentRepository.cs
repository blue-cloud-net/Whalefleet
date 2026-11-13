namespace Whalefleet.Core.Teams;

/// <summary>
/// 团队角色授权仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>继承 <see cref="Common.IRepository{TEntity, TId}"/></b>：CRUD、审计填充与软删除过滤
/// 由 <c>Infrastructure/Persistence/RepositoryBase</c> 统一提供。</para>
/// <para><b>读取方法默认过滤已撤销（软删除）的授权</b>。</para>
/// <para><b>鉴权热路径</b>：每个请求都要查「这些团队在某个作用域授了什么角色」，
/// 因此 <c>(team_id) WHERE is_deleted = 0</c> 必须走索引。</para>
/// </remarks>
public interface ITeamRoleAssignmentRepository : Common.IRepository<TeamRoleAssignment, Guid>
{
    /// <summary>
    /// 列出某团队的全部有效授权。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权列表（可能为空）。</returns>
    Task<IReadOnlyList<TeamRoleAssignment>> ListByTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按团队集合批量查授权。
    /// </summary>
    /// <param name="teamIds">团队主键集合；为空时直接返回空列表（不发 SQL）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部授权（跨团队）；已软删除的团队不出现。</returns>
    /// <remarks>
    /// 鉴权热路径：一次查完用户所属全部团队的有效授权，避免逐团队往返。
    /// </remarks>
    Task<IReadOnlyList<TeamRoleAssignment>> ListByTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询某团队在指定作用域上的某个角色的授权。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="roleId">角色主键。</param>
    /// <param name="hostId">作用域主机主键；<see langword="null"/> 表示查全局授权。</param>
    /// <param name="containerId">容器完整 ID；<see langword="null"/> 表示非容器级。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权记录；不存在或已撤销时返回 <see langword="null"/>。</returns>
    Task<TeamRoleAssignment?> GetAsync(
        Guid teamId,
        Guid roleId,
        Guid? hostId,
        string? containerId = null,
        CancellationToken cancellationToken = default);
}
