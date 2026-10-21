using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Whalefleet.Api.Security;

/// <summary>
/// 从查询参数读取 SignalR 连接的访问令牌。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：浏览器发起的 <c>WebSocket</c> 握手<b>不能自定义请求头</b>，
/// 因此 SignalR 客户端只能把令牌放进查询串（<c>?access_token=…</c>）。
/// <c>JwtBearerHandler</c> 默认只认 <c>Authorization</c> 头，于是握手会 401。</para>
/// <para><b>为什么只对 <c>/hubs</c> 生效</b>：查询串会被写进访问日志、反代日志与浏览器历史，
/// 等于把长期有效的令牌散布到各处。本项目对令牌的处理原则是「密码永不进日志」，
/// 同理适用于访问令牌。因此这里**只**在 SignalR 的握手路径上接受它，
/// 其它路径上的 <c>?access_token=</c> 一律忽略（该处的 <c>Authorization</c> 头仍然照常工作）。</para>
/// <para><b>为什么改写请求头而不是直接改 <c>context.User</c></b>：把令牌搬到
/// <c>Authorization</c> 头后，框架后续的校验流程完全不变——
/// 过期、签名错、issuer 不符都走同一条错误路径（<c>1100</c> / <c>1101</c>），
/// 不需要为长连接再写一套错误映射。</para>
/// </remarks>
internal static class HubTokenQueryReader
{
    /// <summary>
    /// SignalR 握手路径前缀。
    /// </summary>
    /// <remarks>
    /// 与 <c>Program.cs</c> 里 <c>MapHub</c> 的路径保持同一前缀。
    /// <c>/ws</c>（0.8.0 的 exec 终端）**不在此列**——它用的是独立的短期 terminal token，
    /// 不是 access token，鉴权方式在 0.8.0 单独设计。
    /// </remarks>
    private const string HubPathPrefix = "/hubs";

    /// <summary>
    /// 查询参数名。
    /// </summary>
    private const string AccessTokenQueryKey = "access_token";

    /// <summary>
    /// 从查询参数读取令牌并改写为 <c>Authorization</c> 头。
    /// </summary>
    /// <param name="context">消息上下文。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// 已经是标准 <c>Authorization: Bearer …</c> 头的请求**不处理**：
    /// 头优先于查询参数，避免「两头都有、且两者不一致」时的歧义。
    /// </remarks>
    public static Task OnMessageReceivedAsync(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;

        if (!request.Path.StartsWithSegments(HubPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // 已有 Authorization 头 → 以它为准，不动。
        if (request.Headers.ContainsKey("Authorization"))
        {
            return Task.CompletedTask;
        }

        var token = request.Query[AccessTokenQueryKey].ToString();

        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.CompletedTask;
        }

        request.Headers["Authorization"] = $"Bearer {token}";

        return Task.CompletedTask;
    }
}
