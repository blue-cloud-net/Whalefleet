namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 安全策略：密码强度与有效期、首次登录强制改密（0.13.0）。
/// </summary>
/// <param name="MinLength">密码最小长度。</param>
/// <param name="RequireLetter">是否要求包含字母。</param>
/// <param name="RequireDigitOrSymbol">是否要求包含数字或符号。</param>
/// <param name="PasswordExpiryDays">密码有效期（天）；<c>0</c> 表示永不过期。</param>
/// <param name="MustChangeOnFirstLogin">新建用户首次登录是否强制改密。</param>
/// <remarks>
/// <para><b>它是一份「可配的规则」而不是「一段逻辑」</b>：规则本身在
/// <c>Application/Security/PasswordPolicy</c>，这里只描述<b>参数</b>。
/// 把两者分开后，同一套强度判定能同时服务「建用户」「改密」「启动引导」三处路径，
/// 而它们读的是同一份设置——三处各写一套判断迟早漂移。</para>
/// <para><b>默认值刻意保持宽松</b>（≥10 字符 + 字母 + 数字或符号）：
/// 自托管面板的用户就是运维自己，过强的规则只会催生可预测的密码。
/// 真正扛住离线破解的是 Argon2id 参数，不是复杂度规则。</para>
/// </remarks>
public sealed record SecurityPolicyOptions(
    int MinLength = 10,
    bool RequireLetter = true,
    bool RequireDigitOrSymbol = true,
    int PasswordExpiryDays = 0,
    bool MustChangeOnFirstLogin = true)
{
    /// <summary>
    /// 判断这份策略自身是否合法（长度下限、有效期非负等）。
    /// </summary>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>校验放在这里而不是散落在各处</b>：设置端点要校验、启动读取要兜底，
    /// 两处都写一遍判断迟早不一致。
    /// </remarks>
    public bool IsValid() =>
        this.MinLength >= 1 && this.MinLength <= 1024 && this.PasswordExpiryDays >= 0;

    /// <summary>
    /// 判断某次密码变更是否已过期（需要强制改密）。
    /// </summary>
    /// <param name="passwordChangedAt">密码最近一次变更时间（UTC）；<see langword="null"/> 表示从未变更。</param>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <returns>已过期返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>从未改过密的用户不算过期</b>：他的密码是创建时设的，
    /// 按「创建时间 + 有效期」算会让刚建的账号一上来就被要求改密——
    /// 而这正是「首次登录强制改密」开关要管的事，不该由有效期重复管一遍。
    /// </remarks>
    public bool IsPasswordExpired(DateTime? passwordChangedAt, DateTime utcNow)
    {
        if (this.PasswordExpiryDays <= 0 || passwordChangedAt is not { } changedAt)
        {
            return false;
        }

        return changedAt.AddDays(this.PasswordExpiryDays) <= utcNow;
    }
}
