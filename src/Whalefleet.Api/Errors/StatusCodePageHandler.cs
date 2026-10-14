using Whalefleet.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace Whalefleet.Api.Errors;

/// <summary>
/// <c>UseStatusCodePages</c> 的处理程序：把「没有响应体的 4xx/5xx」包装成统一
/// <see cref="ApiResult{T}"/> 错误响应。
/// </summary>
/// <remarks>
/// <para>覆盖的典型场景：未知路由（404）、方法不允许（405）、不支持的媒体类型（415）等——
/// 这些由框架在管道末端直接产生，不经过任何 Controller，因此不会被
/// <c>ExceptionHandlingMiddleware</c> 捕获。</para>
/// <para><c>UseStatusCodePages</c> 本身只在「响应未开始且无响应体」时触发，
/// <c>/healthz</c 与正常 Controller 响应不受影响；本类仍做一次防御性检查。</para>
/// </remarks>
internal static class StatusCodePageHandler
{
    /// <summary>
    /// 处理状态码页面。
    /// </summary>
    /// <param name="context">状态码上下文。</param>
    /// <returns>表示异步操作的任务。</returns>
    public static Task HandleAsync(StatusCodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.HttpContext.Response;

        // 防御性检查：任何已写出内容的响应都不应被改写。
        if (response.HasStarted || response.ContentLength > 0 || response.ContentType is not null)
        {
            return Task.CompletedTask;
        }

        var code = ErrorHttpStatus.ToErrorCode(response.StatusCode);

        response.ContentType = ErrorResponseWriter.JsonContentType;

        // 不修改 response.StatusCode：这里要保留框架产生的原始状态码（如 405）。
        return response.WriteAsync(
            ErrorResponseWriter.Serialize(code, ErrorCodeCatalog.GetDefaultMessage(code)),
            context.HttpContext.RequestAborted);
    }
}
