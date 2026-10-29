namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 当前请求的客户端 IP。
/// </summary>
/// <remarks>
/// <para><b>为什么单独抽一个接口</b>：审计需要「谁从哪个地址来」，
/// 而 IP 是 HTTP 适配器的概念（要读 <c>Connection.RemoteIpAddress</c>、
/// 还要考虑 <c>X-Forwarded-For</c> 与可信代理白名单）。
/// 应用服务不该为了一行日志把 <c>HttpContext</c> 传进来——那会让
/// 每个写用例都多一个「只在 Web 下才成立」的参数。</para>
/// <para><b>实现归属</b>：Api 层基于 <c>HttpContext</c> 实现并替换占位注册
/// （与 <see cref="ICurrentUser"/> 同一做法），Infrastructure 保留
/// 「无请求上下文」的占位实现（返回 <see langword="null"/>，供启动引导与后台任务使用）。</para>
/// <para><b>取不到是正常状态</b>：后端内部调用（后台任务、启动引导）没有来源 IP，
/// 返回 <see langword="null"/> 即可，不应编造一个占位值。</para>
/// </remarks>
public interface ICurrentClientIp
{
    /// <summary>
    /// 当前请求的客户端 IP；无请求上下文或取不到时为 <see langword="null"/>。
    /// </summary>
    string? Ip { get; }
}
