namespace Docker.Manager.Application.Common;

/// <summary>
/// 业务异常基类（占位骨架，0.0.2 完善）。
/// </summary>
/// <remarks>
/// <para>区别于 <c>DomainException</c>：业务异常表达应用层用例执行失败
/// （如外部资源不可用、依赖服务超时）；领域异常表达领域不变量的违反。</para>
/// <para>0.0.2 全局异常处理中间件将捕获本类并映射到 <see cref="ApiResult{T}"/> 的对应错误码。</para>
/// </remarks>
public class BusinessException : Exception
{
    /// <summary>
    /// 业务错误码（对应 <c>docs/api-spec.md</c> 中的错误码分段）。
    /// </summary>
    public int Code { get; }

    public BusinessException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public BusinessException(int code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}