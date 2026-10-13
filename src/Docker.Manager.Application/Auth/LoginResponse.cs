namespace Docker.Manager.Application.Auth;

/// <summary>
/// 登录成功的返回体（对应 <c>docs/api-spec.md</c> 的 <c>POST /api/v1/auth/login</c> 响应 <c>data</c>）。
/// </summary>
/// <param name="AccessToken">JWT 原文。</param>
/// <param name="RefreshToken">刷新令牌原文（服务端只存其哈希）。</param>
/// <param name="ExpiresIn">access token 剩余有效秒数（前端据此安排静默刷新）。</param>
/// <param name="User">已认证用户摘要。</param>
/// <remarks>
/// 用位置记录（positional record）而非带 <c>required</c> 的类：这四个值在构造时<b>必然同时可得</b>，
/// 让编译器强制全部提供，比"允许漏填再靠测试发现"更可靠。
/// </remarks>
public sealed record LoginResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    AuthenticatedUser User);
