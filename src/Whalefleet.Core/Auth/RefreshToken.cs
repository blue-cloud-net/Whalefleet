namespace Whalefleet.Core.Auth;

/// <summary>
/// 刷新令牌（refresh token）记录 —— <b>0.13.0 起它同时就是一个「会话」</b>。
/// </summary>
/// <remarks>
/// <para><b>只存哈希</b>：数据库里保存的是令牌原文的 SHA-256 哈希，<b>不是</b>令牌本身。
/// 令牌原文只在签发响应的那一刻返回给客户端；即使数据库泄露，也无法据此冒用。</para>
/// <para><b>一行 = 一个会话</b>：<see cref="Id"/> 既是会话 id，也是当前 refresh token 的 id；
/// access token 的 <c>sid</c> 声明指向它。轮换时<b>原地更新</b>本行而不是插新行——
/// 否则 access token 里的 <c>sid</c> 会在轮换后指向一个已失效的会话，
/// 「会话列表」也会把一次登录显示成多条记录。</para>
/// <para><b>撤销即黑名单</b>：吊销时把 <see cref="RevokedAt"/> 置位即可，<b>不需要</b>额外的黑名单表。
/// 校验路径只认「未撤销且未过期」的记录。</para>
/// <para><b>轮换痕迹</b>：<see cref="PreviousTokenHash"/> 保存上一次轮换前的哈希，
/// 命中它即说明旧令牌在轮换后再次出现 —— 那是<b>重放</b>的信号。</para>
/// <para><b>设备信息只记录、不校验</b>：<see cref="DeviceFingerprint"/> / <see cref="CreatedIp"/> /
/// <see cref="UserAgent"/> 供会话列表展示，让人能认出「哪一条是我自己」；
/// 它们<b>不参与任何拒绝判定</b>。</para>
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
    /// <b>当前</b>令牌原文的哈希（Base64url 编码的 SHA-256），<b>不是令牌原文</b>。
    /// </summary>
    /// <remarks>
    /// 轮换时本字段被<b>覆盖</b>为新令牌的哈希，旧哈希转入 <see cref="PreviousTokenHash"/>。
    /// </remarks>
    public string TokenHash { get; private set; } = String.Empty;

    /// <summary>
    /// 上一次轮换前的令牌哈希；从未轮换为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <b>只保留一代</b>：命中它即判定重放；更早的旧令牌命中不了任何字段，
    /// 按「未知令牌」处理。
    /// </remarks>
    public string? PreviousTokenHash { get; private set; }

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
    /// <remarks>
    /// <b>0.13.0 起不再写入</b>：新模型用 <see cref="PreviousTokenHash"/> 做重放检测
    /// （轮换是原地更新，没有「新令牌」需要指向）。保留本列是为了避免重建表。
    /// </remarks>
    public string? ReplacedByTokenHash { get; private set; }

    /// <summary>
    /// 最近一次轮换时间（UTC）；从未轮换为 <see langword="null"/>。
    /// </summary>
    public DateTime? RotatedAt { get; private set; }

    /// <summary>
    /// 设备指纹；仅供会话列表展示，<b>不参与鉴权判定</b>。
    /// </summary>
    public string? DeviceFingerprint { get; private set; }

    /// <summary>
    /// 登录来源 IP；仅供会话列表展示。
    /// </summary>
    public string? CreatedIp { get; private set; }

    /// <summary>
    /// 登录时的 User-Agent；仅供会话列表展示。
    /// </summary>
    public string? UserAgent { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Issue"/>。
    /// </summary>
    private RefreshToken()
    {
    }

    /// <summary>
    /// 签发一条刷新令牌记录（同时创建一个会话）。
    /// </summary>
    /// <param name="id">记录主键；它同时是<b>会话 id</b>。</param>
    /// <param name="userId">所属用户主键；不得为 <see cref="Guid.Empty"/>。</param>
    /// <param name="tokenHash">令牌原文的哈希；不得为空。</param>
    /// <param name="expiresAt">过期时间（UTC）。</param>
    /// <param name="deviceFingerprint">设备指纹（可为空）。</param>
    /// <param name="createdIp">登录来源 IP（可为空）。</param>
    /// <param name="userAgent">登录时的 User-Agent（可为空）。</param>
    /// <returns>新建的令牌记录。</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="userId"/> 为空、<paramref name="tokenHash"/> 为空。
    /// </exception>
    public static RefreshToken Issue(
        Guid id,
        Guid userId,
        string tokenHash,
        DateTime expiresAt,
        string? deviceFingerprint = null,
        string? createdIp = null,
        string? userAgent = null)
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
            DeviceFingerprint = Normalize(deviceFingerprint),
            CreatedIp = Normalize(createdIp),
            UserAgent = Normalize(userAgent),
        };
    }

    /// <summary>
    /// 轮换令牌：<b>原地更新</b>本记录，会话 id（<see cref="Id"/>）保持不变。
    /// </summary>
    /// <param name="newTokenHash">新令牌原文的哈希。</param>
    /// <param name="newExpiresAt">新的过期时间（UTC）。</param>
    /// <param name="rotatedAt">轮换时刻（UTC）。</param>
    /// <exception cref="ArgumentException">新令牌哈希为空。</exception>
    /// <remarks>
    /// <b>为什么原地轮换而不是插新行</b>：access token 里的 <c>sid</c> 必须始终指向
    /// 同一个会话——否则轮换后旧 access token 会因「会话不存在」而立刻失效，
    /// 而设计意图是它活到自身过期；且会话列表会把一次登录显示成多条记录。
    /// </remarks>
    public void Rotate(string newTokenHash, DateTime newExpiresAt, DateTime rotatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newTokenHash);

        this.PreviousTokenHash = this.TokenHash;
        this.TokenHash = newTokenHash;
        this.ExpiresAt = newExpiresAt;
        this.RotatedAt = rotatedAt;
    }

    /// <summary>
    /// 撤销该会话。幂等：重复撤销只刷新撤销时间。
    /// </summary>
    /// <param name="revokedAt">撤销时间（UTC）。</param>
    public void Revoke(DateTime revokedAt)
    {
        this.RevokedAt = revokedAt;
    }

    /// <summary>
    /// 更新设备指纹（客户端上报的指纹变化时覆盖）。
    /// </summary>
    /// <param name="fingerprint">新的指纹；为空时不做任何事。</param>
    /// <remarks>
    /// <b>只记录、不判定</b>：指纹变了不构成拒绝理由，只影响会话列表里的展示。
    /// </remarks>
    public void UpdateFingerprint(string? fingerprint)
    {
        var normalized = Normalize(fingerprint);

        if (normalized is null)
        {
            return;
        }

        this.DeviceFingerprint = normalized;
    }

    /// <summary>
    /// 判断某时刻该会话是否仍然可用（未撤销且未过期）。
    /// </summary>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <returns>可用返回 <see langword="true"/>。</returns>
    public bool IsActiveAt(DateTime utcNow) => this.RevokedAt is null && utcNow < this.ExpiresAt;

    /// <summary>
    /// 判断给定哈希是否是<b>上一代</b>令牌（重放检测）。
    /// </summary>
    /// <param name="tokenHash">待比对的令牌哈希。</param>
    /// <returns>命中返回 <see langword="true"/>。</returns>
    public bool MatchesPreviousHash(string tokenHash)
    {
        if (String.IsNullOrWhiteSpace(tokenHash) || this.PreviousTokenHash is null)
        {
            return false;
        }

        return String.Equals(this.PreviousTokenHash, tokenHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// 归一化可选文本：空白视为「没有」。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>去空白后的值；为空时返回 <see langword="null"/>。</returns>
    private static string? Normalize(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
