using Docker.Manager.Application.Common;

namespace Docker.Manager.Api.Errors;

/// <summary>
/// 错误语义分类与 HTTP 状态码之间的双向映射。
/// </summary>
/// <remarks>
/// <para>这一层刻意放在接口层（Api）而非应用层：<see cref="ErrorCategory"/> 只描述业务语义，
/// HTTP 状态码是 Web 协议细节，不应污染 <c>Docker.Manager.Application</c>。</para>
/// <para><b>为什么不能按错误码数值分段推断状态码</b>：同一分段内的 HTTP 语义并不统一——
/// 1002（同段 1000-1099）是 500，1103（同段 1100-1199）是 429，1301（同段 1300-1399）是 502。
/// 因此必须经由 <see cref="ErrorCodeCatalog"/> 的显式分类再映射。</para>
/// </remarks>
public static class ErrorHttpStatus
{
    /// <summary>
    /// 将错误语义分类映射为 HTTP 状态码。
    /// </summary>
    /// <param name="category">错误语义分类。</param>
    /// <returns>对应的 HTTP 状态码；未知分类按 500 处理。</returns>
    public static int ToHttpStatus(ErrorCategory category) => category switch
    {
        ErrorCategory.Success => StatusCodes.Status200OK,
        ErrorCategory.Validation => StatusCodes.Status400BadRequest,
        ErrorCategory.NotFound => StatusCodes.Status404NotFound,
        ErrorCategory.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCategory.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorCategory.Timeout => StatusCodes.Status408RequestTimeout,
        ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCategory.Conflict => StatusCodes.Status409Conflict,
        ErrorCategory.UpstreamFailure => StatusCodes.Status502BadGateway,
        ErrorCategory.Internal => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// 将业务错误码映射为 HTTP 状态码（先经分类，再映射）。
    /// </summary>
    /// <param name="code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <returns>对应的 HTTP 状态码。</returns>
    public static int FromErrorCode(int code) => ToHttpStatus(ErrorCodeCatalog.Classify(code));

    /// <summary>
    /// 将「未产生响应体的 HTTP 状态码」反查为业务错误码，供状态码页面包装使用。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FromErrorCode"/> 方向相反，用于 <c>UseStatusCodePages</c>：
    /// 此时只知道框架产生的状态码（未知路由 404、方法不允许 405 等），需要补一个业务错误码。
    /// </remarks>
    /// <param name="httpStatus">HTTP 状态码。</param>
    /// <returns>对应的业务错误码。</returns>
    public static int ToErrorCode(int httpStatus) => httpStatus switch
    {
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthenticated,
        StatusCodes.Status403Forbidden => ErrorCodes.ResourceForbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status408RequestTimeout => ErrorCodes.OperationTimeout,
        >= StatusCodes.Status500InternalServerError => ErrorCodes.InternalError,
        _ => ErrorCodes.ValidationFailed,
    };
}
