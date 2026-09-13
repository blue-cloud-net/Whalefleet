namespace Docker.Manager.Application.Common;

/// <summary>
/// 统一响应模型（占位骨架，0.0.2 由全局异常处理中间件完整落地）。
/// </summary>
/// <remarks>
/// <para>RPC 风格 API 的所有响应均包装为本结构。完整错误码分段与 HTTP 状态码映射见
/// <c>docs/api-spec.md</c>。</para>
/// <para>当前 commit 仅放 record 占位，避免与 0.0.2 重构冲突。0.0.2 将在 Api 层落地
/// 全局异常处理中间件并与 <see cref="BusinessException"/> 联动。</para>
/// </remarks>
/// <typeparam name="T">业务数据类型。</typeparam>
/// <param name="Success">是否成功（默认 <c>true</c>）。</param>
/// <param name="Code">业务错误码（默认 <c>0</c>，即成功）。</param>
/// <param name="Message">响应消息（默认 <c>"ok"</c>）。</param>
/// <param name="Data">业务数据（成功时填写）。</param>
public record ApiResult<T>(
    bool Success = true,
    int Code = 0,
    string Message = "ok",
    T? Data = default);