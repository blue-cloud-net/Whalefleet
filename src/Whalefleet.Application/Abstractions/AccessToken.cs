namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 已签发的 access token。
/// </summary>
/// <param name="Value">JWT 原文（客户端放入 <c>Authorization: Bearer</c> 头）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <remarks>
/// 把「原文」与「过期时间」一起返回，是为了让调用方不必自己解析 JWT——
/// 解析 JWT 属于令牌服务的职责，调用方重复实现只会造成两处规则漂移。
/// </remarks>
public sealed record AccessToken(string Value, DateTime ExpiresAt);
