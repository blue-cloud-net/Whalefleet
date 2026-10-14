using Whalefleet.Application.Common;

namespace Whalefleet.Application.Security;

/// <summary>
/// 密码强度策略。
/// </summary>
/// <remarks>
/// <para><b>策略是唯一的，且在两处复用</b>：初始管理员引导（启动时）与「修改密码」（请求时）。
/// 两处各写一套判断迟早会漂移——最典型的后果是"引导时能通过、改密时却被拒"。</para>
/// <para><b>为什么这么弱</b>：要求是「≥10 字符 + 字母 + 数字或符号」。刻意的宽松取向——
/// 自托管面板的用户就是运维自己，过强的规则只会催生 `Passw0rd!` 这类可预测密码。
/// 真正扛住离线破解的是 Argon2id 参数，不是复杂度规则。</para>
/// <para><b>不做"常见弱密码表"检查</b>：那需要维护一个词表并随包分发，收益与成本不成比例，
/// 且本项目没有账号自助注册（账号由管理员创建），攻击面本就很小。</para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// 密码最小长度。
    /// </summary>
    public const int MinLength = 10;

    /// <summary>
    /// 以人类可读的方式描述本策略（用于错误消息与启动日志）。
    /// </summary>
    /// <returns>策略描述文本。</returns>
    public static string DescribeRequirement() =>
        $"密码至少 {MinLength} 个字符，且同时包含字母与（数字或符号）";

    /// <summary>
    /// 判断密码是否满足强度要求。
    /// </summary>
    /// <param name="password">待检查的密码；可为 <see langword="null"/>。</param>
    /// <returns>满足返回 <see langword="true"/>。</returns>
    public static bool IsSatisfied(string? password) => FindViolation(password) is null;

    /// <summary>
    /// 校验密码强度，不满足则抛出业务异常。
    /// </summary>
    /// <param name="password">待校验的密码。</param>
    /// <exception cref="BusinessException">
    /// 不满足强度要求（错误码 <see cref="ErrorCodes.WeakPassword"/>）。
    /// </exception>
    /// <remarks>
    /// 请求路径用它（错误码 <c>1202</c> 会返回给客户端）；<b>启动引导不要用它</b>——
    /// 那里需要的是"让进程起不来"，用 <see cref="IsSatisfied"/> 自行判断并抛出
    /// 配置类异常更贴切（业务异常用于请求处理，出现在启动日志里会误导排查方向）。
    /// </remarks>
    public static void EnsureSatisfied(string? password)
    {
        if (FindViolation(password) is { } violation)
        {
            throw new BusinessException(ErrorCodes.WeakPassword, violation);
        }
    }

    /// <summary>
    /// 找出第一处不满足的原因。
    /// </summary>
    /// <param name="password">待检查的密码。</param>
    /// <returns>不满足的原因；满足时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 返回"第一处"而不是全部问题的列表：一次把话说清比列一串更易读，
    /// 且用户改完一处自然会看到下一处。
    /// </remarks>
    private static string? FindViolation(string? password)
    {
        if (String.IsNullOrWhiteSpace(password))
        {
            return "密码不能为空";
        }

        if (password.Length < MinLength)
        {
            return $"密码长度不得少于 {MinLength} 个字符";
        }

        if (!password.Any(Char.IsLetter))
        {
            return "密码必须包含字母";
        }

        if (!password.Any(character => Char.IsDigit(character) || !Char.IsLetterOrDigit(character)))
        {
            return "密码必须包含数字或符号";
        }

        return null;
    }
}
