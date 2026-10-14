namespace Whalefleet.Api.Middleware;

/// <summary>
/// 安全响应头中间件：为<b>所有</b>响应（含错误响应）注入三个基础安全头。
/// </summary>
/// <remarks>
/// <para><b>为什么用 <c>OnStarting</c> 而不是直接给响应头赋值</b>：全局异常处理中间件
/// 在写错误响应前会调用 <c>Response.Clear()</c>，那会连同状态码与<b>全部响应头</b>一起清掉
/// （包括本中间件刚设的）。而 <c>OnStarting</c> 回调不会被 <c>Clear()</c> 清掉，
/// 因此只有挂回调才能保证错误响应也带上这些头——而错误响应恰恰是攻击者更关心的路径。
/// （这个坑曾经真实发生过：测试断言「400 也带安全头」时暴露出来。）</para>
/// <para><b>这三个头各自防什么</b>：
/// <list type="bullet">
///   <item><description><c>X-Content-Type-Options: nosniff</c>——禁止浏览器嗅探 MIME 类型。
///   否则用户可上传的内容被当成 HTML 执行时就成为 XSS 入口。</description></item>
///   <item><description><c>X-Frame-Options: DENY</c>——禁止本页面被任何站点嵌入 iframe，
///   防点击劫持（clickjacking）。管理面板的按钮都是高危操作，这个头尤其必要。</description></item>
///   <item><description><c>Referrer-Policy: no-referrer</c>——不向外站点泄漏本面板的 URL
///   （URL 里可能含 hostId 等内部信息）。</description></item>
/// </list></para>
/// <para><b>不在这里加 CSP</b>：内容安全策略需要与前端实际加载的资源（内联样式、CDN 字体）逐一核对，
/// 盲加会把页面打挂。它属于 1.0.0 的安全审计议题。</para>
/// </remarks>
internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// 初始化中间件。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> 为 <see langword="null"/>。</exception>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>
    /// 注册安全头回调并继续管道。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;

            // 用索引器赋值而不是 Add：同名头只保留一份，不会因重复调用而累积。
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";

            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}