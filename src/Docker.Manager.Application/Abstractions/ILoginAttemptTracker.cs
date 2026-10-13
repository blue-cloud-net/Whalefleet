namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// 登录尝试计数器：按「客户端 IP + 用户名」限制单位时间内的失败次数。
/// </summary>
/// <remarks>
/// <para><b>为什么是「IP + 用户名」两个维度</b>：只按 IP 会让同一 NAT 后的正常用户互相拖累；
/// 只按用户名则攻击者可以用一个 IP 轮换大量用户名（撞库）而不触发限制。两者组合能同时覆盖
/// 「猜某人的密码」与「用少量常见密码扫一批账号」两类攻击。</para>
/// <para><b>阈值与窗口由实现决定</b>：调用方只需要知道"现在该不该拦"，
/// 不需要知道"5 次/分钟"这个数字——把数字放调用方会让它与实现的窗口语义绑死。</para>
/// <para><b>用户名必须归一化后再传</b>：用户名唯一性不区分大小写，若 <c>Alice</c> 与 <c>alice</c>
/// 各自计数，绕过限制只需要换个大小写。归一化由调用方完成（它才知道业务规则）。</para>
/// <para><b>失败才计数</b>：登录成功必须调用 <see cref="Reset"/>，否则用户一旦输错几次，
/// 即使随后登录成功，之前的失败计数仍会在窗口内持续累积。</para>
/// </remarks>
public interface ILoginAttemptTracker
{
    /// <summary>
    /// 判断该「IP + 用户名」是否已超出允许的失败次数（<b>不递增</b>计数）。
    /// </summary>
    /// <param name="clientIp">客户端 IP（无法获取时传占位值，切勿传 <see langword="null"/>）。</param>
    /// <param name="username">已归一化的用户名。</param>
    /// <returns>应拒绝本次登录尝试返回 <see langword="true"/>。</returns>
    bool IsBlocked(string clientIp, string username);

    /// <summary>
    /// 记录一次登录失败。
    /// </summary>
    /// <param name="clientIp">客户端 IP。</param>
    /// <param name="username">已归一化的用户名。</param>
    /// <returns>记录后窗口内的累计失败次数（用于日志）。</returns>
    /// <remarks>
    /// <b>用户名不存在时也必须调用</b>：否则攻击者可通过"是否被限流"反推出用户名是否存在，
    /// 使防枚举的努力前功尽弃。
    /// </remarks>
    int RecordFailure(string clientIp, string username);

    /// <summary>
    /// 登录成功后清零该「IP + 用户名」的失败计数。
    /// </summary>
    /// <param name="clientIp">客户端 IP。</param>
    /// <param name="username">已归一化的用户名。</param>
    void Reset(string clientIp, string username);
}
