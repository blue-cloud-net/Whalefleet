using Docker.Manager.Api.Errors;
using Docker.Manager.Application.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Docker.Manager.Api.Security;

/// <summary>
/// JWT 鉴权失败时的响应改写。
/// </summary>
/// <remarks>
/// <para><b>为什么必须接管</b>：JWT 中间件默认在验证失败时<b>直接写 401 且不带响应体</b>。
/// 那个空响应体会被 <c>UseStatusCodePages</c> 统一包装成 <c>1100</c>，
/// 于是「Token 已过期（<c>1101</c>）」与「未携带 Token（<c>1100</c>）」在客户端看来毫无区别——
/// 前端因此无法决定该跳登录页还是该先静默刷新。这是 0.0.5 的 401 → refresh 重发得以成立的前提。</para>
/// <para><b>为什么要 <c>HandleResponse</c></b>：不调用它，框架仍会在回调返回后补写自己的
/// <c>WWW-Authenticate</c> 与 401，造成响应体与状态码不一致。</para>
/// <para><b>为什么 403 走这里也够</b>：<c>[Authorize(Roles = …)]</c> 不通过时框架走
/// <c>OnForbidden</c>（不写响应体），同样需要包装成 <c>ApiResult</c>。</para>
/// </remarks>
internal static class JwtBearerResponseEvents
{
    /// <summary>
    /// 处理质询（未认证）。
    /// </summary>
    /// <param name="context">质询上下文。</param>
    /// <returns>表示异步操作的任务。</returns>
    public static async Task OnChallengeAsync(JwtBearerChallengeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 防御性检查：响应已经开始写出时不能再改状态码与响应体。
        if (context.Response.HasStarted)
        {
            return;
        }

        // 只有"签名与结构都对、只是过期"这一种情况才回 1101；
        // 其余（未携带、签名错、格式错、issuer/audience 不符）一律 1100，
        // 避免把内部失败原因泄漏给客户端。
        var code = IsExpired(context.AuthenticateFailure)
            ? ErrorCodes.TokenExpired
            : ErrorCodes.Unauthenticated;

        // 阻止框架再补写它自己的 401。
        context.HandleResponse();

        await WriteAsync(context.HttpContext, code).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理禁止访问（已认证但权限不足）。
    /// </summary>
    /// <param name="context">禁止访问上下文。</param>
    /// <returns>表示异步操作的任务。</returns>
    public static async Task OnForbiddenAsync(ForbiddenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
        {
            return;
        }

        await WriteAsync(context.HttpContext, ErrorCodes.ResourceForbidden).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断鉴权失败是否由「令牌已过期」引起。
    /// </summary>
    /// <remarks>
    /// <para><b>必须同时判两种异常类型</b>，因为它们取决于运行时用的是哪个令牌处理器：
    /// 新的 <c>JsonWebTokenHandler</c>（.NET 8+ 的默认）抛
    /// <see cref="SecurityTokenInvalidLifetimeException"/>，旧的 <c>JwtSecurityTokenHandler</c> 抛
    /// <see cref="SecurityTokenExpiredException"/>。不要因为"看起来重复"而删掉任一支。</para>
    /// <para><see cref="SecurityTokenInvalidLifetimeException"/> 同时覆盖「尚未生效（<c>nbf</c> 在未来）」
    /// 与「已过期」两种情况，因此必须再看 <see cref="SecurityTokenInvalidLifetimeException.Expires"/> 是否
    /// 真的已过——把时钟偏差误报成"已过期"会让前端白白走一遭刷新（而刷新也救不了）。</para>
    /// </remarks>
    /// <param name="failure">鉴权失败异常；可能为 <see langword="null"/>。</param>
    /// <returns>确属过期返回 <see langword="true"/>。</returns>
    private static bool IsExpired(Exception? failure) => failure switch
    {
        SecurityTokenExpiredException => true,
        SecurityTokenInvalidLifetimeException invalid =>
            invalid.Expires is not null && invalid.Expires <= DateTime.UtcNow,
        _ => false,
    };

    /// <summary>
    /// 按错误码写出统一的 <c>ApiResult</c> 错误响应。
    /// </summary>
    /// <param name="httpContext">当前 HTTP 上下文。</param>
    /// <param name="code">业务错误码。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task WriteAsync(HttpContext httpContext, int code)
    {
        var response = httpContext.Response;

        // 状态码由错误码逐码映射（不在本类里硬编码 401/403，避免与 ErrorHttpStatus 两份真相）。
        response.StatusCode = ErrorHttpStatus.FromErrorCode(code);
        response.ContentType = ErrorResponseWriter.JsonContentType;

        await response
            .WriteAsync(ErrorResponseWriter.Serialize(code, ErrorCodeCatalog.GetDefaultMessage(code)))
            .ConfigureAwait(false);
    }
}
