namespace Docker.Manager.Domain.Common;

/// <summary>
/// 领域异常基类。所有违反业务不变量的异常继承本类。
/// </summary>
/// <remarks>
/// <para>领域异常由 Application 层统一捕获并映射到 <c>ApiResult&lt;T&gt;</c> 错误码（见
/// <c>Application/Common/ApiResult.cs</c>，0.0.2 落地全局异常处理中间件）。</para>
/// <para>用法：聚合根内不变量校验失败时抛出对应子类，例
/// <c>throw new DomainException("用户已存在");</c>。</para>
/// </remarks>
public class DomainException : Exception
{
    public DomainException()
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}