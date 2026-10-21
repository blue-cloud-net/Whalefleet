namespace Whalefleet.Api.Realtime;

/// <summary>
/// 实时通道（Hub）的路径常量。
/// </summary>
/// <remarks>
/// <para><b>为什么集中成常量</b>：Hub 的路径同时出现在两处——<c>Program.cs</c> 的
/// <c>MapHub</c> 与 <see cref="RealtimeHostResolver"/> 的主机标识解析。
/// 两处各写一遍字面量，改了一处忘了另一处，症状是「订阅总是说缺少主机标识」，
/// 而排查时很难想到「这两个字符串本该是同一个」。</para>
/// <para><b>⚠️ SignalR 的 <c>MapHub</c> 不支持路由参数</b>：路径里的
/// <c>{hostId}</c> 只参与<b>路径匹配</b>，<b>不会</b>出现在
/// <c>HttpContext.Request.RouteValues</c> 里（实测键集合为空）。
/// 因此 Hub 方法里拿不到主机标识，只能<b>从请求路径里解析</b>——
/// 这就是 <see cref="RealtimeHostResolver"/> 存在的原因。</para>
/// </remarks>
public static class RealtimeHubRoutes
{
    /// <summary>
    /// 主机作用域 Hub 的路径前缀（不含主机标识）。
    /// </summary>
    public const string HostScopedPrefix = "/hubs/hosts";

    /// <summary>
    /// 全局通知 Hub 的路径。
    /// </summary>
    public const string Notification = "/hubs/notification";

    /// <summary>
    /// 容器日志 Hub 的路径模板。
    /// </summary>
    public const string ContainerLog = HostScopedPrefix + "/{hostId}/container-log";

    /// <summary>
    /// 容器指标 Hub 的路径模板。
    /// </summary>
    public const string ContainerStats = HostScopedPrefix + "/{hostId}/container-stats";
}
