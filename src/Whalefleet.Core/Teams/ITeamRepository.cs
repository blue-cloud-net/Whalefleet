namespace Whalefleet.Core.Teams;

/// <summary>
/// 团队仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>继承 <see cref="Common.IRepository{TEntity, TId}"/></b>：CRUD、审计填充与软删除过滤
/// 由 <c>Infrastructure/Persistence/RepositoryBase</c> 统一提供。</para>
/// <para><b>软删除语义</b>：读取方法默认过滤已删除团队（<c>WHERE is_deleted = 0</c>）；
/// 团队名唯一索引带 <c>WHERE is_deleted = 0</c>，删除后同名可再次创建。</para>
/// <para><b>位置</b>：接口定义在 <c>Core</c>，实现在 <c>Infrastructure/Persistence</c>；
/// <c>Core</c> 不得引用 Dapper / SQLite。</para>
/// </remarks>
public interface ITeamRepository : Common.IRepository<Team, Guid>
{
    /// <summary>
    /// 按团队标识查询（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="name">团队标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<Team?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断团队标识是否已被占用（不区分大小写，忽略已软删除的团队）。
    /// </summary>
    /// <param name="name">团队标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页列出团队。
    /// </summary>
    /// <param name="skip">跳过的行数（≥ 0）。</param>
    /// <param name="take">取出的行数（> 0）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队列表（可能为空）。</returns>
    Task<IReadOnlyList<Team>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计未删除的团队总数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队数。</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
