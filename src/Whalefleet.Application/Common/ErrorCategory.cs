namespace Whalefleet.Application.Common;

/// <summary>
/// 业务错误码的语义分类。
/// </summary>
/// <remarks>
/// <para>应用层只描述「错误属于哪一类」，<b>不</b>描述「映射到哪个 HTTP 状态码」——
/// HTTP 语义属于接口层，映射由 <c>Whalefleet.Api.Errors.ErrorHttpStatus</c> 完成。
/// 这样分层让错误码分类可在单元测试中独立验证，同时避免应用层依赖 Web 概念。</para>
/// <para>引入本枚举的直接动因：错误码分段内的 HTTP 语义并不统一
/// （例如 <see cref="ErrorCodes.HostUnreachable"/> 与 <see cref="ErrorCodes.HostNotFound"/>
/// 同属 1300 段却是 502 与 404），按分段推断状态码必然出错，因此需要一层显式的语义分类。</para>
/// </remarks>
public enum ErrorCategory
{
    /// <summary>成功（错误码 <see cref="ErrorCodes.Success"/>）。</summary>
    Success,

    /// <summary>请求参数或领域不变量校验失败。</summary>
    Validation,

    /// <summary>请求的资源不存在。</summary>
    NotFound,

    /// <summary>未认证，或凭证无效 / 已过期。</summary>
    Unauthorized,

    /// <summary>触发速率限制。</summary>
    RateLimited,

    /// <summary>操作超时。</summary>
    Timeout,

    /// <summary>已认证但无权限执行该操作。</summary>
    Forbidden,

    /// <summary>资源当前状态不允许该操作（如删除运行中的容器）。</summary>
    /// <remarks>
    /// 与 <see cref="Validation"/> 分开：请求参数本身完全合法，是<b>目标资源的状态</b>不允许——
    /// 映射成 400 会让调用方去查自己的请求体，而真正该做的是先停掉容器再重试。
    /// </remarks>
    Conflict,

    /// <summary>上游依赖（Docker 守护进程等）失败。</summary>
    UpstreamFailure,

    /// <summary>服务器内部错误。</summary>
    Internal,
}
