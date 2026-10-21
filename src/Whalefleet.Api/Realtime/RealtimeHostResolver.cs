using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// Hub 里的主机解析与权限判定。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：Hub 的路径里带 <c>{hostId}</c>（如
/// <c>/hubs/hosts/{hostId}/container-log</c>），但 SignalR 的 Hub 方法<b>拿不到路由值</b>——
/// 只能从 <c>Context.GetHttpContext().Request.RouteValues</c> 里取。
/// 更重要的是：<b>方法级鉴权必须在 Hub 里做</b>。
/// 中间件（<c>CurrentHostMiddleware</c>）只在<b>建立连接时</b>校验一次主机可见性，
/// 而「这个用户能不能读这台主机的容器日志」是<b>每次订阅</b>都要判的。</para>
/// <para><b>为什么失败时抛 <see cref="BusinessException"/></b>：本项目对「业务失败」有统一口径——
/// 错误码由应用层决定，中间件负责翻译。Hub 里抛它，调用方在 Hub 基类里统一转成
/// <see cref="RealtimeActionResult"/> 返回给客户端（错误码原样回带）。</para>
/// </remarks>
public sealed class RealtimeHostResolver
{
    private readonly IDockerHostAccessor _hosts;
    private readonly IPermissionChecker _permissions;

    /// <summary>
    /// 初始化解析器。
    /// </summary>
    /// <param name="hosts">主机访问器。</param>
    /// <param name="permissions">权限判定。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public RealtimeHostResolver(IDockerHostAccessor hosts, IPermissionChecker permissions)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(permissions);

        _hosts = hosts;
        _permissions = permissions;
    }

    /// <summary>
    /// 解析 Hub 上下文里的主机，并校验指定权限。
    /// </summary>
    /// <param name="context">Hub 调用上下文。</param>
    /// <param name="permission">需要的权限（主机作用域）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机实体。</returns>
    /// <exception cref="BusinessException">
    /// 路径里没有主机（<c>1000</c>）、主机不存在（<c>1300</c>）或缺少权限（<c>1502</c>）。
    /// </exception>
    public async Task<DockerHost> ResolveAsync(
        HubCallerContext context,
        string permission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var httpContext = context.GetHttpContext();
        var hostId = ResolveHostId(httpContext);

        if (string.IsNullOrWhiteSpace(hostId))
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                "Hub 路径里缺少主机标识（hostId）。");
        }

        var host = await _hosts.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await _permissions
            .EnsurePermissionAsync(ResolveUserId(context), host.Id, permission, cancellationToken)
            .ConfigureAwait(false);

        return host;
    }

    /// <summary>
    /// 从请求里解析主机标识。
    /// </summary>
    /// <param name="httpContext">HTTP 上下文。</param>
    /// <returns>主机标识；取不到时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么不能只读路由值</b>：SignalR 的 <c>MapHub</c> <b>不支持路由参数</b>——
    /// 路径里的 <c>{hostId}</c> 只参与路径匹配，<b>不会</b>出现在
    /// <c>Request.RouteValues</c> 里（实测该字典的键集合为空）。
    /// 因此只能从请求路径里解析。
    /// <para>
    /// 路由值仍然先读一次：万一将来 SignalR 改了行为（或换成别的映射方式），
    /// 这一行就能直接生效，不必再动解析逻辑。
    /// </para>
    /// </remarks>
    private static string? ResolveHostId(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var routeValue = httpContext.Request.RouteValues["hostId"] as string;
        if (!string.IsNullOrWhiteSpace(routeValue))
        {
            return routeValue;
        }

        var path = httpContext.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var prefix = RealtimeHubRoutes.HostScopedPrefix + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 去掉前缀后取到下一个斜杠为止；协商请求会多一段 /negotiate，正好被切掉。
        var rest = path[prefix.Length..];
        var separator = rest.IndexOf('/', StringComparison.Ordinal);
        var hostId = separator < 0 ? rest : rest[..separator];

        return string.IsNullOrWhiteSpace(hostId) ? null : Uri.UnescapeDataString(hostId);
    }

    /// <summary>
    /// 从 Hub 上下文取登录用户主键。
    /// </summary>
    /// <param name="context">Hub 调用上下文。</param>
    /// <returns>用户主键；取不到时为 <see cref="Guid.Empty"/>（判权限时按「无权限」处理）。</returns>
    /// <remarks>
    /// <b>为什么不用 <c>ICurrentUser</c></b>：那个实现读的是 <c>HttpContextAccessor</c>，
    /// 在 Hub 的后台推送任务里（方法已返回、HttpContext 可能已回收）读到的是 <see langword="null"/>。
    /// 而这里只在<b>方法调用期间</b>取一次，取完就存进订阅里，之后不再需要 HttpContext。
    /// </remarks>
    public static Guid ResolveUserId(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.User?.FindFirst(JwtClaimNames.Subject)?.Value;

        return Guid.TryParse(subject, out var userId) ? userId : Guid.Empty;
    }
}
