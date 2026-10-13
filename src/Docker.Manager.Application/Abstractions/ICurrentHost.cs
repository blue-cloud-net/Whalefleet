namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// 当前请求针对的 Docker 主机。
/// </summary>
/// <remarks>
/// <para><b>与 <see cref="ICurrentUser"/> 并列</b>：一个回答「谁在操作」，一个回答「操作哪台主机」。</para>
/// <para><b>取值来源是路由值，不是请求头</b>：由 Api 层中间件从
/// <c>HttpContext.GetRouteData().Values["hostId"]</c> 填充。旧的 <c>X-Host-Id</c> 请求头方案已废弃——
/// 主机标识只出现在路径里，避免出现「路径说 A、请求头说 B」这种两处真相。</para>
/// <para><b>无主机上下文是正常状态</b>：<see cref="HostId"/> 为 <see langword="null"/> 表示本次请求
/// 不针对某一台主机——全局作用域端点（<c>/api/v1/auth/*</c>、<c>/api/v1/admin/*</c>）、
/// 后台维护任务与启动引导都属于这一类。</para>
/// <para><b>服务层不注入它</b>：应用服务的方法显式接收 <c>hostId</c> 参数。
/// 后台任务与单元测试里没有 HTTP 请求，注入式取值会静默拿到 <see langword="null"/> 或别人的值；
/// 显式参数则让「必须有主机」在编译期就可见。</para>
/// </remarks>
public interface ICurrentHost
{
    /// <summary>
    /// 当前主机标识（即主机名，已被规范化为库中大小写）；无主机上下文为 <see langword="null"/>。
    /// </summary>
    string? HostId { get; }
}
