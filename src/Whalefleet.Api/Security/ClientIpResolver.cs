namespace Whalefleet.Api.Security;

/// <summary>
/// 解析调用方 IP，用于登录失败计数与日志。
/// </summary>
/// <remarks>
/// <para><b>取值来源是 TCP 连接</b>（<c>Connection.RemoteIpAddress</c>），<b>不直接</b>读取
/// <c>X-Forwarded-For</c>：该头可被客户端任意伪造，在没有「可信代理白名单」的前提下采信它，
/// 等于把限流键交给攻击者——伪造一个新 IP 就能无限重试。</para>
/// <para><b>反代场景（0.0.15 起）</b>：部署在 HTTPS 反代之后时，
/// <c>RemoteIpAddress</c> 原本会是<b>反代自己的 IP</b>，于是所有用户的失败计数落进同一个桶——
/// 5 次失败后全体用户在 1 分钟窗口内都无法登录。现由
/// <see cref="Configuration.ForwardedHeadersExtensions.UseConfiguredForwardedHeaders"/>
/// 在管道最前按 <c>ReverseProxy</c> 白名单修正该值；<b>未配置白名单时本类看到的仍是真实 TCP 对端</b>。
/// 因此这里无需也不该自行解析该请求头。</para>
/// <para>无法取得地址时返回 <see cref="Unknown"/> 而不是 <see langword="null"/>：
/// 限流器需要一个稳定的键，<c>null</c> 会让每次调用落到不同的键上而使限流失效。</para>
/// </remarks>
internal static class ClientIpResolver
{
    /// <summary>
    /// 无法解析出 IP 时使用的占位值。
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// 解析调用方 IP。
    /// </summary>
    /// <param name="httpContext">当前 HTTP 上下文。</param>
    /// <returns>IP 字符串；无法取得时为 <see cref="Unknown"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> 为 <see langword="null"/>。</exception>
    public static string Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? Unknown;
    }
}
