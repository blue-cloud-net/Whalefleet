namespace Whalefleet.Application.Common;

/// <summary>
/// 业务异常：表示应用层用例执行失败。
/// </summary>
/// <remarks>
/// <para>表达<b>用例执行失败</b>：外部资源不可用、依赖服务超时、请求参数不合法、
/// 业务规则不允许（如「主机名重复」「用户已存在」）等。本项目不设独立的领域异常类型，
/// <c>Core</c> 层实体的不变量校验使用 BCL 的 <see cref="ArgumentException"/>，
/// 由中间件统一映射为 <c>1000</c> / HTTP 400。</para>
/// <para>全局异常处理中间件捕获本类后，用 <see cref="Code"/> 经 <see cref="ErrorCodeCatalog"/>
/// 解析出 HTTP 状态码与兜底文案，并包装为 <see cref="ApiResult{T}"/>。
/// 因此应用层<b>不要</b>构造任何 HTTP 相关对象。</para>
/// <para>推荐使用静态工厂（如 <see cref="NotFound"/>）而非直接调用构造函数，
/// 以避免错误码与语义脱节。</para>
/// <para><b>安全约束</b>：除 <see cref="ErrorCodes.InternalError"/> 外，
/// <see cref="Exception.Message"/> 会原样返回给客户端，因此<b>禁止</b>把密码、Token、
/// 连接字符串等敏感信息写入消息；<see cref="ErrorCodes.InternalError"/> 的消息会被
/// 中间件替换为固定文案，但仍不应依赖该行为。</para>
/// </remarks>
public class BusinessException : Exception
{
    /// <summary>
    /// 业务错误码（对应 <c>docs/api-spec.md</c> 的错误码分段，常量见 <see cref="ErrorCodes"/>）。
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// 使用指定的错误码与消息初始化 <see cref="BusinessException"/>。
    /// </summary>
    /// <param name="code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <param name="message">错误消息（将返回给客户端，不得包含敏感信息）。</param>
    public BusinessException(int code, string message)
        : base(message)
    {
        this.Code = code;
    }

    /// <summary>
    /// 使用指定的错误码、消息与内部异常初始化 <see cref="BusinessException"/>。
    /// </summary>
    /// <param name="code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <param name="message">错误消息（将返回给客户端，不得包含敏感信息）。</param>
    /// <param name="innerException">内部异常（仅写入服务端日志，不返回给客户端）。</param>
    public BusinessException(int code, string message, Exception innerException)
        : base(message, innerException)
    {
        this.Code = code;
    }

    /// <summary>
    /// 构造参数校验失败异常。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.ValidationFailed"/> 的业务异常。</returns>
    public static BusinessException Validation(string message) =>
        new(ErrorCodes.ValidationFailed, message);

    /// <summary>
    /// 构造资源不存在异常。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.NotFound"/> 的业务异常。</returns>
    public static BusinessException NotFound(string message) =>
        new(ErrorCodes.NotFound, message);

    /// <summary>
    /// 构造未认证异常。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.Unauthenticated"/> 的业务异常。</returns>
    public static BusinessException Unauthenticated(string message) =>
        new(ErrorCodes.Unauthenticated, message);

    /// <summary>
    /// 构造无权限异常。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.ResourceForbidden"/> 的业务异常。</returns>
    public static BusinessException Forbidden(string message) =>
        new(ErrorCodes.ResourceForbidden, message);

    /// <summary>
    /// 构造操作超时异常。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.OperationTimeout"/> 的业务异常。</returns>
    public static BusinessException Timeout(string message) =>
        new(ErrorCodes.OperationTimeout, message);

    /// <summary>
    /// 构造主机连通性失败异常。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.HostUnreachable"/> 的业务异常。</returns>
    public static BusinessException HostUnreachable(string message) =>
        new(ErrorCodes.HostUnreachable, message);

    /// <summary>
    /// 构造服务器内部错误异常。
    /// </summary>
    /// <remarks>
    /// 该错误码的响应消息会被全局异常处理中间件替换为固定文案（<c>服务器内部错误</c>），
    /// 以避免内部细节泄漏到客户端。
    /// </remarks>
    /// <param name="message">错误消息（仅用于服务端日志）。</param>
    /// <returns>错误码为 <see cref="ErrorCodes.InternalError"/> 的业务异常。</returns>
    public static BusinessException Internal(string message) =>
        new(ErrorCodes.InternalError, message);
}