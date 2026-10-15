using Whalefleet.Api.Security;
using Whalefleet.Application.Abstractions;

namespace Whalefleet.Api.Middleware;

/// <summary>
/// 从路由值 <c>hostId</c> 解析「当前主机」，写入请求级的
/// <see cref="ICurrentHost"/>。
/// </summary>
/// <remarks>
/// <para><b>为什么在这解析而不是各控制器各解析一次</b>：主机作用域端点的路径形如
/// <c>/api/v1/hosts/{hostId}/...</c>，「这台主机存在吗」「这个人有权用它吗」对它们全部成立。
/// 放在中间件里做，未知主机统一变成 <c>1300</c>/404、无权限统一变成 <c>1501</c>/403，
/// 控制器可以直接信任路径里的 <c>hostId</c>，不必各自重复一遍校验。</para>
/// <para><b>代价</b>：主机作用域的每个请求多一次按主机名的查询（<c>ux_hosts_name</c> 唯一索引命中）。
/// 与后续真正的 Docker 调用相比可以忽略；若 0.1.0 的权限校验需要复用实体，再考虑把实体本身缓存到
/// 请求级。</para>
/// <para><b>全局作用域不受影响</b>：没有 <c>{hostId}</c> 路由段时 <c>HostId</c> 保持
/// <see langword="null"/>，这正是「无主机上下文」的语义（不是错误）。</para>
/// <para><b>管道位置</b>：必须在路由匹配之后（否则读不到路由值）、在端点之前。当前实现里
/// 由 <c>Program.cs</c> 放在 <c>UseAuthentication()</c> 之后——
/// 这样 0.1.0 加可见性校验时能直接读 <c>HttpContext.User</c>。</para>
/// </remarks>
internal sealed class CurrentHostMiddleware
{
    /// <summary>
    /// 主机作用域路由模板中的路由值键名。
    /// </summary>
    /// <remarks>
    /// 对外公开以便控制器路由模板与这里保持同一个字面量来源——
    /// 路径模板写 <c>{hostId}</c>、这里读 <c>"hostId"</c>，两处一旦拼错就会静默变成
    /// 「总是无主机上下文」，用常量至少让引用关系可见。
    /// </remarks>
    public const string RouteValueKey = "hostId";

    private readonly RequestDelegate _next;

    /// <summary>
    /// 初始化中间件。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> 为 <see langword="null"/>。</exception>
    public CurrentHostMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>
    /// 解析当前主机、校验可见性，并继续管道。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="currentHost">当前主机上下文的写入侧（从请求作用域解析）。</param>
    /// <param name="hosts">主机访问器（用于校验主机确实存在）。</param>
    /// <param name="currentUser">当前登录用户（可见性校验需要角色与主体）。</param>
    /// <param name="permissions">资源级权限校验。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentHostAccessor currentHost,
        IDockerHostAccessor hosts,
        ICurrentUser currentUser,
        IResourcePermissionService permissions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentHost);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(permissions);

        var hostId = context.GetRouteData().Values[RouteValueKey] as string;

        if (!String.IsNullOrWhiteSpace(hostId))
        {
            // 解析同时校验「主机存在」，失败抛 BusinessException(1300) → 全局异常中间件转 404。
            // 用库里的 Name 而不是原始路径片段：URL 里的大小写不应影响后续取值。
            var host = await hosts.ResolveAsync(hostId, context.RequestAborted).ConfigureAwait(false);

            // 可见性校验挂在解析之后、端点之前：0.0.9 起的每个主机作用域端点自动受保护，
            // 不必各写一遍。未认证请求（UserId 为空）在这里会被当作非管理员拒掉（fail-closed）；
            // 带 [Authorize] 的端点其实在更早的 UseAuthorization 就已经 401。
            await permissions
                .EnsureHostVisibleAsync(currentUser.UserId ?? Guid.Empty, host, context.RequestAborted)
                .ConfigureAwait(false);

            currentHost.Set(host.Name);
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="CurrentHostMiddleware"/> 的管道注册扩展。
/// </summary>
/// <remarks>
/// 中间件本身是 <see langword="internal"/>（外部无需知道它），
/// 但管道注册必须是公开入口：集成测试要能按<b>与生产完全一样</b>的顺序把中间件接进管道，
/// 而不是另搞一套只在测试里存在的接线——那种「测试里一套、生产里一套」正是漏测的来源。
/// </remarks>
public static class CurrentHostMiddlewareExtensions
{
    /// <summary>
    /// 启用「从路由值解析当前主机」。需在 <c>UseRouting()</c> 之后、端点之前调用。
    /// </summary>
    /// <param name="app">应用构建器。</param>
    /// <returns>同一 <see cref="IApplicationBuilder"/> 实例，便于链式调用。</returns>
    public static IApplicationBuilder UseCurrentHost(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CurrentHostMiddleware>();
    }
}
