using Whalefleet.Api.Terminal;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Api.Middleware;

/// <summary>
/// 服务端错误兜底审计：把 <b>5xx</b> 写进活动日志。
/// </summary>
/// <remarks>
/// <para><b>为什么只记 5xx，而不是 roadmap 原话的「4xx / 5xx」</b>：审计表是只增不减的，
/// 而 4xx 里绝大多数是<b>正常业务反馈</b>——容器不存在、权限不足、参数写错。
/// 其中「权限不足」已经由应用服务以 <c>outcome=denied</c> 精确记录（带资源名），
/// 而「列表页轮询撞上一个刚被删掉的容器」会稳定地每秒产生一条 404。
/// 把 4xx 全塞进来，真正需要追溯的 500 会被淹没在同一张表里。</para>
/// <para><b>为什么自己 catch 再 rethrow</b>：本中间件位于
/// <c>ExceptionHandlingMiddleware</c> <b>内层</b>。异常会先经过这里再交出去，
/// 但此时外层还没写响应，<c>Response.StatusCode</c> 仍是 200——只看状态码会漏掉所有未处理异常。
/// 因此异常路径显式记一次，再把原异常抛回外层（那里才负责写响应体）。</para>
/// <para><b>为什么排除非 API 路径</b>：SPA 深链、<c>/ws</c>、<c>/hubs</c> 的失败不是「业务操作」，
/// 记进来只会让活动日志里混入一堆与资源无关的行。</para>
/// </remarks>
public sealed class AuditFallbackMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuditFallbackMiddleware> _logger;

    /// <summary>
    /// 初始化兜底审计中间件。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AuditFallbackMiddleware(RequestDelegate next, ILogger<AuditFallbackMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// 执行中间件。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="audit">审计写入器。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task InvokeAsync(HttpContext context, IAuditLogger audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audit);

        if (!ShouldAudit(context))
        {
            await _next(context).ConfigureAwait(false);

            return;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 异常路径：外层还没写响应，只能在这里记。
            await RecordAsync(audit, context, ErrorCodes.InternalError, exception.Message)
                .ConfigureAwait(false);

            // 原样抛回：响应体由 ExceptionHandlingMiddleware 统一写，这里不重复处理。
            throw;
        }

        if (context.Response.StatusCode >= 500)
        {
            await RecordAsync(audit, context, ErrorCodes.InternalError, $"HTTP {context.Response.StatusCode}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 是否需要对本次请求做兜底审计。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <returns>需要返回 <see langword="true"/>。</returns>
    private static bool ShouldAudit(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments(TerminalRoutes.Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 写一条 <c>http.error</c> 审计。
    /// </summary>
    /// <param name="audit">审计写入器。</param>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="reason">原因（进 <c>detail</c>）。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task RecordAsync(
        IAuditLogger audit,
        HttpContext context,
        int errorCode,
        string reason)
    {
        try
        {
            await audit.RecordAsync(new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.HttpError,
                Outcome = AuditOutcome.Failure,
                ErrorCode = errorCode,
                TargetType = AuditTargetTypes.Session,
                TargetName = $"{context.Request.Method} {context.Request.Path.Value}",
                Detail = reason,
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // IAuditLogger 本身已是 best-effort；这里再兜一层是为了**不掩盖原异常**
            // （若审计抛出，调用方的 catch 会看到审计的异常而不是真正的问题）。
            _logger.LogWarning(exception, "兜底审计写入失败");
        }
    }
}
