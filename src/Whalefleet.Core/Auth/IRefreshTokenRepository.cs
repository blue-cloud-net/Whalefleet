namespace Whalefleet.Core.Auth;

/// <summary>
/// 刷新令牌仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>位置</b>：接口定义在 <c>Core</c>，实现在 <c>Infrastructure/Persistence</c>。</para>
/// <para><b>CRUD 部分见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>GetAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> 由基接口统一声明。</para>
/// <para><b>无软删除，因此删除语义与其他仓储相反</b>：令牌记录不参与软删除——撤销靠
/// <see cref="RefreshToken.Revoke"/>，过期记录由 <see cref="DeleteExpiredAsync"/>
/// <b>物理删除</b>。基接口的 <c>RemoveAsync</c> 在本仓储实现为<b>物理删除单条</b>
/// （与 <see cref="DeleteExpiredAsync"/> 同族），而非打软删除标记。</para>
/// <para><b>查询键是哈希</b>：客户端提交的是令牌原文，调用方先算哈希再查库；
/// 仓储与数据库都<b>不接触</b>令牌原文。</para>
/// </remarks>
public interface IRefreshTokenRepository : Common.IRepository<RefreshToken, Guid>
{
    /// <summary>
    /// 按令牌哈希查询记录。
    /// </summary>
    /// <param name="tokenHash">令牌原文的哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌记录；不存在时返回 <see langword="null"/>。</returns>
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销某用户的全部有效令牌（「登出所有设备」/ 改密后强制下线）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="utcNow">撤销时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际被撤销的记录数。</returns>
    Task<int> RevokeAllForUserAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>
    /// 物理删除已过期（或已撤销且已过期）的令牌记录。
    /// </summary>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除的记录数。</returns>
    Task<int> DeleteExpiredAsync(DateTime utcNow, CancellationToken cancellationToken = default);
}
