using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Users;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Infrastructure.Permissions;

/// <summary>
/// 资源级权限校验实现（**当前只做 host 可见性，且默认拒绝**）。
/// </summary>
/// <remarks>
/// <para><b>现状</b>：管理员对所有主机可见；非管理员一律拒绝。逐资源授权的数据基础
/// （<c>user_host_permissions</c>）排在 0.3.0 建表，此前没有"哪些主机对该用户可见"的答案，
/// 因此只能给一个安全的方向——<b>fail-closed</b>：拿不准就不放行。</para>
/// <para><b>为什么不先放行</b>：放行是"零成本的错"，而它错得很隐蔽——界面一切正常，
/// 直到某天发现普通用户能停掉生产容器。拒绝则立刻表现为 403，使用者会来问"怎么授权"，
/// 而 0.3.0 正是回答这个问题的地方。</para>
/// <para><b>实现为何在 Infrastructure</b>：0.3.0 起它要读权限表（依赖仓储），
/// 那时就不属于应用层了；现在就放在它最终该在的位置，避免以后又一次搬迁。</para>
/// </remarks>
internal sealed class ResourcePermissionService : IResourcePermissionService
{
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ResourcePermissionService> _logger;

    /// <summary>
    /// 初始化权限校验服务。
    /// </summary>
    /// <param name="currentUser">当前登录用户（角色判定只能来自请求上下文）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ResourcePermissionService(ICurrentUser currentUser, ILogger<ResourcePermissionService> logger)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(logger);

        _currentUser = currentUser;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task EnsureHostVisibleAsync(
        Guid userId,
        DockerHost host,
        CancellationToken cancellationToken = default)
    {
        if (this.IsHostVisible(userId, host))
        {
            return Task.CompletedTask;
        }

        // 记 Warning 而不是 Debug：被拒绝的越权尝试值得留痕（尤其是排查"为什么某人看不到主机"）。
        // 只记用户名/主键与主机名，不记任何凭据。
        _logger.LogWarning(
            "拒绝访问未授权主机（用户={UserId}，主机={HostName}，逐资源授权表尚未落地，当前一律拒绝）",
            userId,
            host.Name);

        throw new BusinessException(
            ErrorCodes.HostForbidden,
            $"无权访问主机「{host.Name}」。逐资源授权将在 0.3.0 提供。");
    }

    /// <inheritdoc />
    public bool IsHostVisible(Guid userId, DockerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // 与 EnsureHostVisibleAsync 共用同一条规则：这里放行、那里拒绝，
        // 就会出现“列表里看不到、但直接访问又能进”的偏差。
        return this.IsAdministrator;
    }

    /// <summary>
    /// 当前请求是否来自管理员。
    /// </summary>
    /// <remarks>
    /// 角色只能由 <see cref="ICurrentUser"/> 给出（它来自 JWT claim）；方法参数
    /// <c>userId</c> 是"待校验的主体"，两者在调用点一致，但角色判定不该依赖参数。
    /// </remarks>
    private bool IsAdministrator =>
        String.Equals(_currentUser.Role, Role.AdminName, StringComparison.OrdinalIgnoreCase);
}
