namespace Whalefleet.Core.Teams;

/// <summary>
/// 团队成员仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>它是纯关系表，不继承 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>team_members</c> 的主键是复合键（团队 + 用户），且没有独立生命周期与审计价值——
/// 移除成员关系就是删掉这行。给它套用单主键实体基类只会得到一堆别扭的适配。</para>
/// <para><b>硬删除</b>：与 <c>host_tag_assignments</c> 同口径。</para>
/// <para><b>为什么它很重要</b>：这是鉴权热路径——每个请求都要查「这个人属于哪些团队」，
/// 因此 <c>(user_id)</c> 必须走索引。</para>
/// </remarks>
public interface ITeamMemberRepository
{
    /// <summary>
    /// 列出某团队的成员主键。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成员主键列表（可能为空）。</returns>
    Task<IReadOnlyList<Guid>> ListMemberIdsAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按用户集合批量查团队主键。
    /// </summary>
    /// <param name="userIds">用户主键集合；为空时直接返回空列表（不发 SQL）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队主键列表（跨用户，可能重复）。</returns>
    /// <remarks>
    /// 鉴权热路径：一次查完该用户所属的全部团队，避免逐个用户查一次。
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListTeamIdsByUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把成员加入团队（<b>幂等</b>）。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实新增了一行返回 <see langword="true"/>；已是成员返回 <see langword="false"/>。</returns>
    Task<bool> AddAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把成员移出团队（<b>幂等</b>）。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实删除了一行返回 <see langword="true"/>；本就不是成员返回 <see langword="false"/>。</returns>
    Task<bool> RemoveAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断某用户是否为某团队成员。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是成员返回 <see langword="true"/>。</returns>
    Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量查询多个团队各自的成员数。
    /// </summary>
    /// <param name="teamIds">团队主键集合；为空时直接返回空字典（不发 SQL）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队主键 → 成员数。</returns>
    /// <remarks>团队列表页要显示「多少人」；逐个 <c>ListMemberIdsAsync</c> 是 N+1。</remarks>
    Task<IReadOnlyDictionary<Guid, int>> CountMembersAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default);
}
