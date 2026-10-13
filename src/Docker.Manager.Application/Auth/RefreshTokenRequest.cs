namespace Docker.Manager.Application.Auth;

/// <summary>
/// 携带刷新令牌的请求（<c>refresh</c> 与 <c>logout</c> 共用）。
/// </summary>
/// <remarks>
/// 字段可空是刻意的：请求体由客户端决定，缺字段是常态而非异常。
/// 若声明为非空 <see cref="string"/>，"没传 refreshToken" 会表现为空引用异常（500），
/// 而正确语义是参数校验失败（1000）。
/// </remarks>
public sealed record RefreshTokenRequest
{
    /// <summary>
    /// 刷新令牌原文。
    /// </summary>
    public string? RefreshToken { get; init; }
}
