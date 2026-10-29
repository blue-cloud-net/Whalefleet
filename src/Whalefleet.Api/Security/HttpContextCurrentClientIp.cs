using Whalefleet.Application.Abstractions;

namespace Whalefleet.Api.Security;

/// <summary>
/// 基于 <see cref="HttpContext"/> 的客户端 IP 解析（审计用）。
/// </summary>
/// <remarks>
/// <para><b>复用 <see cref="ClientIpResolver"/></b>：登录限流与审计必须看到<b>同一个</b> IP。
/// 两处各解析一次，一旦其中一处将来改成读 <c>X-Forwarded-For</c>，
/// 就会出现「限流按 A 计数、审计记成 B」的错位，而审计恰恰是事后追这条线用的。</para>
/// <para><b>拿不到请求上下文时返回 <see langword="null"/></b>：那说明是在没有 HTTP 请求的
/// 路径上（后台任务），此时「没有来源 IP」就是正确答案。</para>
/// </remarks>
internal sealed class HttpContextCurrentClientIp : ICurrentClientIp
{
    private readonly IHttpContextAccessor _accessor;

    /// <summary>
    /// 初始化客户端 IP 解析。
    /// </summary>
    /// <param name="accessor">HTTP 上下文访问器。</param>
    /// <exception cref="ArgumentNullException"><paramref name="accessor"/> 为 <see langword="null"/>。</exception>
    public HttpContextCurrentClientIp(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        _accessor = accessor;
    }

    /// <inheritdoc />
    public string? Ip
    {
        get
        {
            var context = _accessor.HttpContext;

            return context is null ? null : ClientIpResolver.Resolve(context);
        }
    }
}
