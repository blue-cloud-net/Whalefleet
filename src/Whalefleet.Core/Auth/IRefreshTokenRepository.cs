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
    /// 按令牌哈希查询记录（当前代令牌）。
    /// </summary>
    /// <param name="tokenHash">令牌原文的哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌记录；不存在时返回 <see langword="null"/>。</returns>
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按<b>上一代</b>令牌哈希查询记录（重放检测专用，0.13.0）。
    /// </summary>
    /// <param name="tokenHash">令牌原文的哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中返回那条会话记录；没命中返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 命中即说明「旧令牌在轮换后再次出现」——合法客户端轮换后不再持有旧令牌，
    /// 因此这只可能是令牌泄漏或客户端实现有 bug。
    /// </remarks>
    Task<RefreshToken?> FindByPreviousHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销某用户的全部有效令牌（「登出所有设备」/ 改密后强制下线）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="utcNow">撤销时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际被撤销的记录数。</returns>
    Task<int> RevokeAllForUserAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销<b>单个</b>会话（按会话 id，0.13.0）。
    /// </summary>
    /// <param name="id">会话 id。</param>
    /// <param name="utcNow">撤销时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实撤销了一行返回 <see langword="true"/>；已撤销 / 不存在返回 <see langword="false"/>。</returns>
    /// <remarks><b>幂等</b>：对已撤销的会话再次调用不会报错，只是返回 <see langword="false"/>。</remarks>
    Task<bool> RevokeByIdAsync(Guid id, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原地轮换会话的令牌（0.13.0）。
    /// </summary>
    /// <param name="id">会话 id。</param>
    /// <param name="newTokenHash">新令牌原文的哈希。</param>
    /// <param name="newExpiresAt">新的过期时间（UTC）。</param>
    /// <param name="rotatedAt">轮换时刻（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实更新了一行返回 <see langword="true"/>；会话不存在返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// <b>会话 id 不变</b>：access token 的 <c>sid</c> 因此始终指向同一个会话。
    /// 旧哈希被保留在 <c>previous_token_hash</c> 供重放检测。
    /// </remarks>
    Task<bool> RotateAsync(
        Guid id,
        string newTokenHash,
        DateTime newExpiresAt,
        DateTime rotatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某用户的<b>活跃</b>会话（未撤销且未过期，0.13.0）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话记录列表（可能为空）。</returns>
    Task<IReadOnlyList<RefreshToken>> ListActiveByUserAsync(
        Guid userId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新会话的设备指纹（0.13.0）。
    /// </summary>
    /// <param name="id">会话 id。</param>
    /// <param name="fingerprint">新的指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实更新了一行返回 <see langword="true"/>。</returns>
    /// <remarks><b>只记录、不判定</b>：指纹变化不构成拒绝理由。</remarks>
    Task<bool> UpdateFingerprintAsync(
        Guid id,
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 物理删除已过期（或已撤销且已过期）的令牌记录。
    /// </summary>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除的记录数。</returns>
    Task<int> DeleteExpiredAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按会话 id 集合物理删除记录（0.13.0 的过期清理）。
    /// </summary>
    /// <param name="ids">会话 id 集合；为空时直接返回 0（不发 SQL）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除的记录数。</returns>
    Task<int> DeleteByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);
}
