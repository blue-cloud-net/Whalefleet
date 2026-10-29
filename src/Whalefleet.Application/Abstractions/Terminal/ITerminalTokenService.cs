namespace Whalefleet.Application.Abstractions.Terminal;

/// <summary>
/// 一次终端会话的授权凭据（<b>不含</b>令牌原文）。
/// </summary>
/// <param name="UserId">发起会话的用户主键。</param>
/// <param name="HostId">主机标识（主机名，即 <c>DockerHost.Name</c>）。</param>
/// <param name="ContainerId">容器标识（完整 ID / 短 ID / 名称，由调用方决定粒度）。</param>
/// <param name="Shell">要启动的 shell 绝对路径（已过白名单校验）。</param>
/// <param name="Command">非交互模式下要执行的一条命令；<see langword="null"/> 表示开交互式 shell。</param>
/// <remarks>
/// <para><b>为什么把「容器」与「命令」放进令牌而不是留在查询串里</b>：查询串是客户端完全可控的。
/// 若令牌只证明「这个人是某人」，那么一条为 A 容器签发的令牌就能被拿去连 B 容器，
/// 或者把 <c>cmd</c> 换成另一个可执行文件——令牌因此不成其为授权，只是一张门禁卡。
/// 把三者绑进令牌，令牌就成了「对这次具体会话的一次性许可」。</para>
/// <para><b>为什么 HostId 是主机名而不是 Guid</b>：与 URL 里的 <c>hostId</c> 同源，
/// 便于把「路径里的主机」与「令牌里的主机」直接比对（不一致即视为令牌被改造过）。</para>
/// </remarks>
public sealed record TerminalSessionGrant(
    Guid UserId,
    string HostId,
    string ContainerId,
    string Shell,
    string? Command);

/// <summary>
/// 已签发的 terminal token。
/// </summary>
/// <param name="Value">令牌原文（客户端放进 WebSocket 的 <c>?token=</c>）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <remarks>
/// 与 <see cref="AccessToken"/> 同构：把原文与过期时间一起返回，
/// 调用方不必自己解析 JWT——解析是令牌服务的职责。
/// </remarks>
public sealed record TerminalToken(string Value, DateTime ExpiresAt);

/// <summary>
/// terminal token 的签发与校验（<b>仅用于</b> <c>/ws</c> 终端握手）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要一种独立的令牌</b>：浏览器建立 WebSocket 时不能自定义请求头，
/// 令牌只能出现在 URL 里；而 URL 会被写进访问日志、反代日志与浏览器历史。
/// 把 15 分钟的 access token 放进去，等于把「整个 API 的钥匙」抄在门口。
/// terminal token 因此是：短时（默认 5 分钟）、<b>只对一条 WebSocket 有效</b>、
/// 且<b>不被主鉴权接受</b>。</para>
/// <para><b>「不被主鉴权接受」由 audience 保证</b>：实现使用独立的 <c>aud</c>，
/// 而 JwtBearer 的 <c>ValidAudience</c> 是主 audience，因此它会在校验阶段直接判失败，
/// 不需要一段「手工比对 typ 再拒绝」的代码。少一段这种代码，就少一个「漏判即提权」的位置。</para>
/// <para><b>无状态</b>：不落库、不可撤销。5 分钟的窗口内即使令牌泄漏，也只能对
/// 同一条已绑定的会话生效（容器与命令都在令牌里），且用户仍须通过 <c>container.write</c>。</para>
/// </remarks>
public interface ITerminalTokenService
{
    /// <summary>
    /// 签发 terminal token。
    /// </summary>
    /// <param name="grant">会话授权内容（会被写进令牌声明）。</param>
    /// <param name="utcNow">当前 UTC 时间（用于计算过期时刻）。</param>
    /// <returns>令牌原文与过期时间。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grant"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">授权内容里的字段为空或全空白。</exception>
    TerminalToken Create(TerminalSessionGrant grant, DateTime utcNow);

    /// <summary>
    /// 校验 terminal token。
    /// </summary>
    /// <param name="token">客户端提交的令牌原文。</param>
    /// <param name="utcNow">当前 UTC 时间（用于判定过期）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验通过时返回授权内容；签名错、过期、audience 不符等一律返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么返回 <see langword="null"/> 而不是抛异常</b>：调用方（WebSocket 端点）
    /// 对<b>所有</b>失败形态的处理完全相同——回 <c>1104</c>。让实现区分「过期 / 签名错 / 格式错」
    /// 只会诱使调用方把差异透给客户端，而那正是攻击者需要的反馈。
    /// </remarks>
    Task<TerminalSessionGrant?> ValidateAsync(
        string token,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
