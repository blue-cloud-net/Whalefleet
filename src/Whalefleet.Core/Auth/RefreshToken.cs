namespace Whalefleet.Core.Auth;

/// <summary>
/// 刷新令牌（refresh token）记录。
/// </summary>
/// <remarks>
/// <para><b>只存哈希</b>：数据库里保存的是令牌原文的 SHA-256 哈希，<b>不是</b>令牌本身。
/// 令牌原文只在签发响应的那一刻返回给客户端；即使数据库泄露，也无法据此冒用。</para>
/// <para><b>撤销即黑名单</b>：登出时把 <see cref="RevokedAt"/> 置位即可，<b>不需要</b>额外的黑名单表。
/// 校验路径只认「未撤销且未过期」的记录。</para>
/// <para><b>轮换</b>：刷新成功时旧令牌被撤销并通过 <see cref="ReplacedByTokenHash"/> 指向新令牌，
/// 便于排查「令牌被重复使用」这类异常链路。</para>
/// <para><b>为什么不用全量审计基类</b>：令牌是<b>一次性凭据</b>而非业务实体——撤销状态由
/// <see cref="RevokedAt"/> 表达，过期后由维护任务<b>物理删除</b>，无需软删除与修改审计。
/// 只需创建审计（谁、何时申请了令牌）。</para>
/// </remarks>
public sealed class RefreshToken : CreationAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 令牌所属用户的主键。
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 令牌原文的哈希（Base64url 编码的 SHA-256），<b>不是令牌原文</b>。
    /// </summary>
    public string TokenHash { get; private set; } = String.Empty;

    /// <summary>
    /// 过期时间（UTC）。
    /// </summary>
    public DateTime ExpiresAt { get; private set; }

    /// <summary>
    /// 撤销时间（UTC）；未撤销为 <see langword="null"/>。
    /// </summary>
    public DateTime? RevokedAt { get; private set; }

    /// <summary>
    /// 轮换后取代本令牌的新令牌哈希；非轮换撤销为 <see langword="null"/>。
    /// </summary>
    public string? ReplacedByTokenHash { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Issue"/>。
    /// </summary>
    private RefreshToken()
    {
    }

    /// <summary>
    /// 签发一条刷新令牌记录。
    /// </summary>
    /// <param name="id">记录主键。</param>
    /// <param name="userId">所属用户主键；不得为 <see cref="Guid.Empty"/>。</param>
    /// <param name="tokenHash">令牌原文的哈希；不得为空。</param>
    /// <param name="expiresAt">过期时间（UTC）。</param>
    /// <returns>新建的令牌记录。</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="userId"/> 为空、<paramref name="tokenHash"/> 为空。
    /// </exception>
    public static RefreshToken Issue(Guid id, Guid userId, string tokenHash, DateTime expiresAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("刷新令牌必须归属某个用户", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        return new RefreshToken
        {
            Id = id,
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>
    /// 撤销该令牌。幂等：重复撤销会刷新撤销时间与取代者。
    /// </summary>
    /// <param name="revokedAt">撤销时间（UTC）。</param>
    /// <param name="replacedByTokenHash">轮换产生的新令牌哈希；非轮换撤销传 <see langword="null"/>。</param>
    public void Revoke(DateTime revokedAt, string? replacedByTokenHash = null)
    {
        this.RevokedAt = revokedAt;
        this.ReplacedByTokenHash = replacedByTokenHash;
    }

    /// <summary>
    /// 判断某时刻该令牌是否仍然可用（未撤销且未过期）。
    /// </summary>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <returns>可用返回 <see langword="true"/>。</returns>
    public bool IsActiveAt(DateTime utcNow) => this.RevokedAt is null && utcNow < this.ExpiresAt;
}
