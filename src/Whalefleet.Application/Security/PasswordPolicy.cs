using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Security;

/// <summary>
/// 密码强度策略。
/// </summary>
/// <remarks>
/// <para><b>策略是唯一的，且在多处复用</b>：初始管理员引导（启动时）、建用户与改密（请求时）
/// 都走这一套判断。各处各写一套迟早会漂移——最典型的后果是
/// 「引导时能通过、改密时却被拒」。</para>
/// <para><b>参数来自 <see cref="SecurityPolicyOptions"/>（0.13.0 起可配）</b>：
/// 规则本身在这里，规则<b>的取值</b>存在实例设置里。分开后，同一套判定能服务三条路径，
/// 而它们读的是同一份设置。</para>
/// <para><b>默认取向是宽松</b>：≥10 字符 + 字母 + 数字或符号。刻意的宽松——
/// 自托管面板的用户就是运维自己，过强的规则只会催生 <c>Passw0rd!</c> 这类可预测密码。
/// 真正扛住离线破解的是 Argon2id 参数，不是复杂度规则。</para>
/// <para><b>不做「常见弱密码表」检查</b>：那需要维护一个词表并随包分发，收益与成本不成比例，
/// 且本项目没有账号自助注册（账号由管理员创建），攻击面本就很小。</para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// 密码最小长度（<b>默认策略</b>；实际取值以 <see cref="SecurityPolicyOptions"/> 为准）。
    /// </summary>
    public const int MinLength = 10;

    /// <summary>
    /// 默认策略（未配置时使用）。
    /// </summary>
    public static SecurityPolicyOptions DefaultOptions { get; } = new();

    /// <summary>
    /// 以人类可读的方式描述本策略（用于错误消息与启动日志）。
    /// </summary>
    /// <param name="options">策略参数；省略时用默认策略。</param>
    /// <returns>策略描述文本。</returns>
    public static string DescribeRequirement(SecurityPolicyOptions? options = null)
    {
        var effective = options ?? DefaultOptions;

        var requirements = new List<string> { $"至少 {effective.MinLength} 个字符" };

        if (effective.RequireLetter)
        {
            requirements.Add("包含字母");
        }

        if (effective.RequireDigitOrSymbol)
        {
            requirements.Add("包含数字或符号");
        }

        return "密码" + String.Join("且", requirements);
    }

    /// <summary>
    /// 判断密码是否满足强度要求。
    /// </summary>
    /// <param name="password">待检查的密码；可为 <see langword="null"/>。</param>
    /// <param name="options">策略参数；省略时用默认策略。</param>
    /// <returns>满足返回 <see langword="true"/>。</returns>
    public static bool IsSatisfied(string? password, SecurityPolicyOptions? options = null) =>
        FindViolation(password, options) is null;

    /// <summary>
    /// 校验密码强度，不满足则抛出业务异常。
    /// </summary>
    /// <param name="password">待校验的密码。</param>
    /// <param name="options">策略参数；省略时用默认策略。</param>
    /// <exception cref="BusinessException">
    /// 不满足强度要求（错误码 <see cref="ErrorCodes.WeakPassword"/>）。
    /// </exception>
    /// <remarks>
    /// 请求路径用它（错误码 <c>1202</c> 会返回给客户端）；<b>启动引导不要用它</b>——
    /// 那里需要的是"让进程起不来"，用 <see cref="IsSatisfied"/> 自行判断并抛出
    /// 配置类异常更贴切（业务异常用于请求处理，出现在启动日志里会误导排查方向）。
    /// </remarks>
    public static void EnsureSatisfied(string? password, SecurityPolicyOptions? options = null)
    {
        if (FindViolation(password, options) is { } violation)
        {
            throw new BusinessException(ErrorCodes.WeakPassword, violation);
        }
    }

    /// <summary>
    /// 找出第一处不满足的原因。
    /// </summary>
    /// <param name="password">待检查的密码。</param>
    /// <param name="options">策略参数；省略时用默认策略。</param>
    /// <returns>不满足的原因；满足时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 返回"第一处"而不是全部问题的列表：一次把话说清比列一串更易读，
    /// 且用户改完一处自然会看到下一处。
    /// </remarks>
    private static string? FindViolation(string? password, SecurityPolicyOptions? options)
    {
        var effective = options ?? DefaultOptions;

        if (String.IsNullOrWhiteSpace(password))
        {
            return "密码不能为空";
        }

        if (password.Length < effective.MinLength)
        {
            return $"密码长度不得少于 {effective.MinLength} 个字符";
        }

        if (effective.RequireLetter && !password.Any(Char.IsLetter))
        {
            return "密码必须包含字母";
        }

        if (effective.RequireDigitOrSymbol
            && !password.Any(character => Char.IsDigit(character) || !Char.IsLetterOrDigit(character)))
        {
            return "密码必须包含数字或符号";
        }

        return null;
    }
}
