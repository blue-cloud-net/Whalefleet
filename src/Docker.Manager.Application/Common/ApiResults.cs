namespace Docker.Manager.Application.Common;

/// <summary>
/// <see cref="ApiResult"/> 与 <see cref="ApiResult{T}"/> 的统一构造入口。
/// </summary>
/// <remarks>
/// <para>工厂方法刻意放在<b>非泛型</b>静态类中，而非 <see cref="ApiResult{T}"/> 之上：
/// 泛型类型中的静态成员会触发 CA1000（且每个封闭类型各存一份），
/// 而独立工厂类是本项目的既定用法，也与 ASP.NET Core 的 <c>Results</c> 习惯一致。</para>
/// <para>用法示例：<c>ApiResults.Ok(dto)</c>、<c>ApiResults.Fail&lt;object&gt;(ErrorCodes.InternalError, "服务器内部错误")</c>。</para>
/// </remarks>
public static class ApiResults
{
    /// <summary>
    /// 构造不带数据的成功响应。
    /// </summary>
    /// <returns>成功响应实例。</returns>
    public static ApiResult Ok() => new(true, ErrorCodes.Success, "ok");

    /// <summary>
    /// 构造带数据的成功响应。
    /// </summary>
    /// <typeparam name="T">业务数据类型。</typeparam>
    /// <param name="data">业务数据。</param>
    /// <returns>成功响应实例。</returns>
    public static ApiResult<T> Ok<T>(T? data = default) =>
        new(true, ErrorCodes.Success, "ok", data);

    /// <summary>
    /// 构造不带数据的失败响应。
    /// </summary>
    /// <param name="code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <param name="message">错误消息（不得包含敏感信息，将返回给客户端）。</param>
    /// <returns>失败响应实例。</returns>
    public static ApiResult Fail(int code, string message) => new(false, code, message);

    /// <summary>
    /// 构造带数据占位的失败响应，用于需要保留 <c>"data": null</c> 字段的接口。
    /// </summary>
    /// <typeparam name="T">业务数据类型。</typeparam>
    /// <param name="code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <param name="message">错误消息（不得包含敏感信息，将返回给客户端）。</param>
    /// <returns>失败响应实例（<c>Data</c> 为 <c>null</c>）。</returns>
    public static ApiResult<T> Fail<T>(int code, string message) =>
        new(false, code, message, default);
}
