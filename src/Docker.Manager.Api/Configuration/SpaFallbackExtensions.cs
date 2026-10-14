using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace Docker.Manager.Api.Configuration;

/// <summary>
/// SPA 深链回退（<c>/hosts/local/containers</c> → <c>index.html</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 <c>MapFallbackToFile</c></b>：它会把<b>所有</b>未匹配路由
/// 都导向 index.html，包括 <c>/api/v1/xxx</c> 里打错的路径。症状极其恶劣——
/// 接口返回 200 + 一段 HTML，日志里全是成功记录，而前端表现为 JSON 解析失败。
/// 调这类问题几乎不可能靠读日志定位。</para>
/// <para><b>因此这里显式排除接口前缀</b>：<c>/api</c> 与 <c>/healthz</c> 一律交给
/// 正常路由，未命中时由 <c>UseStatusCodePages</c> 包成 <c>ApiResult</c>（404 / 错误码）。</para>
/// <para><b>该排除规则成立的前提</b>：所有控制器都挂在 <c>/api/v1</c> 下，
/// 健康检查在 <c>/healthz</c>。新增<b>非</b>这些前缀的端点时必须同步更新
/// <see cref="ApiPathPrefixes"/>，否则它会被静默吞成前端页面。</para>
/// </remarks>
internal static class SpaFallbackExtensions
{
    /// <summary>
    /// 不参与 SPA 回退的路径前缀。
    /// </summary>
    internal static readonly string[] ApiPathPrefixes = ["/api", "/healthz"];

    /// <summary>
    /// 注册 SPA 回退。
    /// </summary>
    /// <param name="app">应用管道构建器。</param>
    /// <param name="spaRoot">前端产物绝对路径。</param>
    /// <param name="indexFile">入口文件名。</param>
    /// <returns>同一 <paramref name="app"/> 实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 必须挂在两处之间：
    /// <list type="bullet">
    /// <item><b>在 <c>UseStaticFiles</c> 之后</b>：真实存在的资源（<c>/assets/*.js</c>）
    /// 已被静态文件中间件处理，不会走到这里；这里只负责「没有对应文件的路径」。</item>
    /// <item><b>在 <c>UseCurrentHost</c> 之前</b>：前端的 <c>/hosts/{hostId}/...</c>
    /// 与接口的 <c>/api/v1/hosts/{hostId}/...</c> 同前缀，而 <c>UseCurrentHost</c>
    /// 会把<b>任何</b>请求的 <c>hostId</c> 路由值当主机去查库。放在它之后，
    /// 每次刷新 SPA 深链都会先抛「主机不存在」（404），页面根本打不开。</item>
    /// </list>
    /// </remarks>
    public static IApplicationBuilder UseSpaFallback(
        this IApplicationBuilder app,
        string spaRoot,
        string indexFile)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexFile);

        var fileProvider = new PhysicalFileProvider(spaRoot);

        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            foreach (var prefix in ApiPathPrefixes)
            {
                if (path.StartsWithSegments(prefix))
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }
            }

            // 带扩展名的一律视为「资源」：例如 /assets/logo-404.png。
            // 回退成 index.html 会让浏览器把 HTML 当图片解析，
            // 报出与真实问题（资源确实不存在）毫无关系的 MIME 错误。
            if (Path.HasExtension(path.Value))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";

            // ⚠️ GetFileInfo 收的是**相对于 provider 根**的路径。
            // 传绝对路径会被再拼一次根目录，得到 spaRoot/spaRoot/index.html。
            await using var stream = fileProvider.GetFileInfo(indexFile).CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        });
    }
}