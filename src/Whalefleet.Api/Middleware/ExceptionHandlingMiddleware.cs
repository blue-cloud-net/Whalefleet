using System.Diagnostics;
using System.Text.Json;
using Whalefleet.Api.Errors;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Middleware;

/// <summary>
/// 全局异常处理中间件：捕获管道中所有未处理异常，统一包装为
/// <see cref="ApiResult{T}"/> 并映射到合适的 HTTP 状态码。
/// </summary>
/// <remarks>
/// <para><b>注册位置</b>：必须是管道中<b>第一个</b>中间件（最外层），否则注册在它之前的
/// 中间件抛出的异常不会被捕获。</para>
/// <para><b>为什么用中间件而非 <c>IExceptionHandler</c></b>：本项目需要精确控制管道顺序，
/// 并且需要捕获 <see cref="ArgumentException"/> / <see cref="JsonException"/> 等框架异常；
/// 显式中间件让顺序与映射规则一目了然，且无需额外注册 ProblemDetails 服务。</para>
/// <para><b>安全约束</b>：</para>
/// <list type="bullet">
///   <item><description>响应体<b>绝不</b>包含堆栈、内部异常消息或类型名；仅返回业务文案。</description></item>
///   <item><description>错误码 <see cref="ErrorCodes.InternalError"/> 的消息一律替换为固定文案，
///   即使异常由 <see cref="BusinessException"/> 抛出也不例外。</description></item>
///   <item><description>服务端日志记录完整异常（含堆栈）与 <c>TraceId</c>，4xx 记 Warning（不含堆栈，避免噪声），
///   5xx 记 Error（含堆栈）。</description></item>
/// </list>
/// <para><b>关于业务不变量</b>：<c>Core</c> 层表达参数与不变量违规一律使用 BCL 的
/// <see cref="ArgumentException"/>，因此不存在需要单独映射的"领域异常"分支——
/// 业务执行失败由 <see cref="BusinessException"/> 自带错误码表达，其余按上面的映射顺序兜底。</para>
/// </remarks>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;

    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    /// <summary>
    /// 初始化 <see cref="ExceptionHandlingMiddleware"/>。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <param name="logger">日志记录器。</param>
    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// 执行中间件。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端主动断开（浏览器关闭、用户取消导出等）：响应通道已失效，不应也无法写响应体。
            // 这不是错误，记 Debug 避免污染日志。
            _logger.LogDebug(
                "请求已被客户端中止（Method={Method}，Path={Path}）",
                context.Request.Method,
                context.Request.Path.Value);
        }
        catch (Exception exception)
        {
            // 无法在 catch 块之外使用无参 throw，因此把重抛决策留在本 catch 内。
            if (!await this.HandleExceptionAsync(context, exception).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// 记录异常并尝试写出统一错误响应。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="exception">待处理的异常。</param>
    /// <returns>已写出错误响应返回 <c>true</c>；若响应已开始写出（无法改写）则返回 <c>false</c>，
    /// 由调用方重新抛出原始异常。</returns>
    private async Task<bool> HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (code, message) = MapException(exception);
        var httpStatus = ErrorHttpStatus.FromErrorCode(code);
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        if (httpStatus >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "未处理异常（Code={Code}，HttpStatus={HttpStatus}，TraceId={TraceId}，Method={Method}，Path={Path}）",
                code,
                httpStatus,
                traceId,
                context.Request.Method,
                context.Request.Path.Value);
        }
        else
        {
            // 4xx 属于可预期的客户端/业务错误，不记录堆栈，避免日志噪声与被误报为告警。
            _logger.LogWarning(
                "请求处理失败（Code={Code}，HttpStatus={HttpStatus}，TraceId={TraceId}，Method={Method}，Path={Path}，Reason={Reason}）",
                code,
                httpStatus,
                traceId,
                context.Request.Method,
                context.Request.Path.Value,
                exception.Message);
        }

        if (context.Response.HasStarted)
        {
            // 响应已经开始写出（例如流式输出中途失败），此时无法再改写状态码与响应体。
            // 记录后返回 false，由调用方重新抛出原始异常，保留堆栈并由宿主中止连接。
            _logger.LogCritical(
                "响应已开始写出，无法改写为统一错误响应；连接将被中止（Code={Code}，TraceId={TraceId}，Path={Path}）",
                code,
                traceId,
                context.Request.Path.Value);

            return false;
        }

        context.Response.Clear();
        context.Response.StatusCode = httpStatus;
        context.Response.ContentType = ErrorResponseWriter.JsonContentType;

        await context.Response
            .WriteAsync(ErrorResponseWriter.Serialize(code, message), context.RequestAborted)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// 将异常映射为业务错误码与对客户端可见的消息。
    /// </summary>
    /// <remarks>
    /// 匹配顺序即优先级：越具体的异常越靠前。<see cref="ArgumentNullException"/> 与
    /// <see cref="TaskCanceledException"/> 分别派生自 <see cref="ArgumentException"/> 与
    /// <see cref="OperationCanceledException"/>，此处按基类统一处理。
    /// </remarks>
    /// <param name="exception">待映射的异常。</param>
    /// <returns>业务错误码与响应消息。</returns>
    private static (int Code, string Message) MapException(Exception exception) => exception switch
    {
        // 业务异常自带错误码；消息由业务方提供，属于「可控文案」。
        BusinessException business => (business.Code, ResolveBusinessMessage(business)),

        // 参数类异常统一按 400 处理（ArgumentNullException 派生自 ArgumentException）。
        // Core 层的不变量违规也走这里。
        ArgumentException => (
            ErrorCodes.ValidationFailed,
            ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.ValidationFailed)),

        KeyNotFoundException => (
            ErrorCodes.NotFound,
            ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.NotFound)),

        // 请求体反序列化失败 / 框架层面的错误请求（如 Content-Type 不匹配）。
        JsonException => (
            ErrorCodes.ValidationFailed,
            ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.ValidationFailed)),
        BadHttpRequestException => (
            ErrorCodes.ValidationFailed,
            ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.ValidationFailed)),

        // 未被「客户端断开」过滤器捕获的超时（例如 HttpClient 超时）。
        TaskCanceledException => (
            ErrorCodes.OperationTimeout,
            ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.OperationTimeout)),

        // 兜底：不泄漏任何内部细节。
        _ => (
            ErrorCodes.InternalError,
            ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.InternalError)),
    };

    /// <summary>
    /// 解析 <see cref="BusinessException"/> 对客户端可见的消息。
    /// </summary>
    /// <remarks>
    /// <see cref="ErrorCodes.InternalError"/> 属于「服务端故障」语义，其消息可能包含内部细节，
    /// 因此强制替换为固定文案；其余错误码的消息由业务方保证不含敏感信息。
    /// </remarks>
    /// <param name="exception">业务异常。</param>
    /// <returns>可返回给客户端的消息。</returns>
    private static string ResolveBusinessMessage(BusinessException exception) =>
        exception.Code == ErrorCodes.InternalError
            ? ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.InternalError)
            : exception.Message;
}
