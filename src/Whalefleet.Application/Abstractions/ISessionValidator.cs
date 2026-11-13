namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 会话校验的结论（0.13.0）。
/// </summary>
/// <remarks>
/// <b>为什么用枚举而不是布尔</b>：布尔只能表达「有效 / 无效」，
/// 而这里需要区分四种「无效」——会话不存在、已吊销、已过期、账号已停用——
/// 因为它们对应**不同的错误码与不同的界面提示**。
/// 「账号已停用」尤其不能混进「未认证」：前者要让管理员看见并去解锁，
/// 后者只会让用户以为自己掉线了。</remarks>
public enum SessionOutcome
{
    /// <summary>会话有效。</summary>
    Valid = 0,

    /// <summary>会话不存在（未知 id，或已被过期清理删除）。</summary>
    NotFound = 1,

    /// <summary>会话已被吊销（登出 / 强制登出 / 改密 / 停用）。</summary>
    Revoked = 2,

    /// <summary>会话已过期。</summary>
    Expired = 3,

    /// <summary>账号已停用。</summary>
    AccountDisabled = 4,

    /// <summary>账号必须先修改密码。</summary>
    PasswordChangeRequired = 5,
}

/// <summary>
/// 会话校验的结果（0.13.0）。
/// </summary>
/// <param name="Outcome">校验结论。</param>
/// <param name="UserId">会话所属用户主键；结论为 <see cref="SessionOutcome.NotFound"/> 时为 <see cref="Guid.Empty"/>。</param>
/// <param name="Fingerprint">会话记录中的设备指纹（仅供覆盖更新用）。</param>
/// <param name="MustChangePassword">账号是否必须先改密。</param>
public sealed record SessionValidationResult(
    SessionOutcome Outcome,
    Guid UserId = default,
    string? Fingerprint = null,
    bool MustChangePassword = false)
{
    /// <summary>
    /// 构造「会话有效」的结果。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="fingerprint">会话记录中的设备指纹。</param>
    /// <param name="mustChangePassword">账号是否必须先改密。</param>
    /// <returns>有效结果。</returns>
    public static SessionValidationResult Valid(
        Guid userId,
        string? fingerprint = null,
        bool mustChangePassword = false) =>
        new(SessionOutcome.Valid, userId, fingerprint, mustChangePassword);
}

/// <summary>
/// 会话校验器（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>它回答的是「这个登录态还能用吗」</b>，不是「这个人有哪些权限」。
/// 权限判定走 <c>IPermissionChecker</c>，两者互不干涉。</para>
/// <para><b>放在 <c>Application/Abstractions</c></b>：接口层（中间件）需要它，
/// 而实现要访问仓储与缓存。</para>
/// </remarks>
public interface ISessionValidator
{
    /// <summary>
    /// 校验一个会话。
    /// </summary>
    /// <param name="sessionId">会话 id（来自 access token 的 <c>sid</c> 声明）。</param>
    /// <param name="fingerprint">
    /// 客户端上报的设备指纹；<b>只用于覆盖更新记录，不参与判定</b>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验结果。</returns>
    Task<SessionValidationResult> ValidateAsync(
        Guid sessionId,
        string? fingerprint,
        CancellationToken cancellationToken = default);
}
