using System.Collections.Frozen;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Infrastructure.Permissions;

/// <summary>
/// 权限判定的实现（**唯一一份生效规则**）。
/// </summary>
/// <remarks>
/// <para><b>生效规则</b>（另两处都转调 <see cref="GetEffectivePermissionsAsync"/>，不复制）：</para>
/// <list type="number">
///   <item><description>全局分配（<c>host_id IS NULL</c>）贡献角色的 <b>Global</b> 权限；</description></item>
///   <item><description>主机分配贡献角色的 <b>Host</b> 域权限，<b>且只在该主机上</b>；
///   它的 Global 权限被忽略——否则「把 admin 分配到某台主机」就是一次静默的系统级提权；</description></item>
///   <item><description>多条分配的权限取<b>并集</b>。</description></item>
/// </list>
/// <para><b>为什么缓存</b>：本类是 <b>Scoped</b>，一次请求内用惰性求值把结果钉住。
/// 主机列表过滤会把 N 台主机变成 N 次判定，没有缓存就是 N 次往返；
/// 而请求级缓存不会带来「改了权限当前请求还没生效」的问题——
/// 请求级语义本来就是一次快照。</para>
/// <para><b>⚠️ 缓存键必须带 hostId</b>：<c>(userId)</c> 单独做键会把「在 A 主机是 operator」
/// 误用到「在 B 主机」的判定上，那是<b>越权</b>，不是精度问题。</para>
/// <para><b>管理员短路</b>：全局持有内置 <c>admin</c> 角色即返回全部权限。
/// 短路发生在筛选角色权限<b>之前</b>——不短路的话，每次判定都要把 16 条权限全部查出来。</para>
/// </remarks>
internal sealed class PermissionChecker : IPermissionChecker
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserRoleAssignmentRepository _assignments;
    private readonly IRoleRepository _roles;
    private readonly ILogger<PermissionChecker> _logger;

    /// <summary>
    /// 请求内的权限集缓存。键为 <c>(userId, hostId)</c>，其中 <c>hostId</c> 为 <see langword="null"/> 表示全局。
    /// </summary>
    private readonly Dictionary<(Guid UserId, Guid? HostId), IReadOnlySet<string>> _cache = [];

    /// <summary>
    /// 请求内的管理员判定缓存（免得每个判定都重查一次分配）。
    /// </summary>
    private bool? _isAdministrator;

    /// <summary>
    /// 构造权限判定服务。
    /// </summary>
    /// <param name="currentUser">当前登录用户。</param>
    /// <param name="assignments">分配仓储。</param>
    /// <param name="roles">角色仓储。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public PermissionChecker(
        ICurrentUser currentUser,
        IUserRoleAssignmentRepository assignments,
        IRoleRepository roles,
        ILogger<PermissionChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(logger);

        _currentUser = currentUser;
        _assignments = assignments;
        _roles = roles;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> HasPermissionAsync(
        Guid userId,
        Guid? hostId,
        string permission,
        CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        var effective = await this.GetEffectivePermissionsAsync(userId, hostId, cancellationToken)
            .ConfigureAwait(false);

        return effective.Contains(permission.Trim().ToLowerInvariant());
    }

    /// <inheritdoc />
    public async Task EnsurePermissionAsync(
        Guid userId,
        Guid? hostId,
        string permission,
        CancellationToken cancellationToken = default)
    {
        if (await this.HasPermissionAsync(userId, hostId, permission, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // 记 Warning 而不是 Debug：被拒绝的越权尝试值得留痕（尤其是排查「为什么某人看不到主机」）。
        // 只记用户主键与权限标识，不记任何凭据。
        _logger.LogWarning(
            "拒绝访问：用户 {UserId} 缺少权限 {Permission}（作用域：{Scope}）",
            userId,
            permission,
            hostId is { } id ? id : "全局");

        throw new BusinessException(
            ErrorCodes.OperationForbidden,
            $"缺少「{permission}」权限，请联系管理员开通。");
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? hostId,
        CancellationToken cancellationToken = default)
    {
        // Guid.Empty 是「未认证」的哨兵。拿它去查表必然查不到——
        // 让这里自然返回空集合，而不是给未认证用户开一条「无分配即全部可见」的通路。
        if (userId == Guid.Empty)
        {
            return FrozenSet<string>.Empty;
        }

        var key = (userId, hostId);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var assignments = await _assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        // 只保留「在生效范围内」的分配：全局分配永远生效，主机分配仅在该主机上生效
        var effectiveAssignments = assignments
            .Where(assignment => hostId is null
                ? assignment.IsGlobal
                : assignment.AppliesTo(hostId.Value))
            .ToArray();

        if (effectiveAssignments.Length == 0)
        {
            _cache[key] = FrozenSet<string>.Empty;
            return _cache[key];
        }

        var roleIds = effectiveAssignments.Select(assignment => assignment.RoleId).Distinct().ToArray();
        var roles = await _roles.QueryListByIdsAsync(roleIds, cancellationToken).ConfigureAwait(false);

        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            var granted = await _roles.GetPermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false);

            foreach (var permission in granted)
            {
                // 规则 2：主机分配只认 Host 域权限。全局分配（hostId is null）两条都认。
                if (hostId is null || Permission.ScopeOf(permission) == PermissionScope.Host)
                {
                    permissions.Add(permission);
                }
            }
        }

        var result = permissions.Count == 0
            ? FrozenSet<string>.Empty
            : permissions.ToFrozenSet(StringComparer.Ordinal);

        _cache[key] = result;
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DockerHost>> FilterVisibleHostsAsync(
        Guid userId,
        IReadOnlyList<DockerHost> hosts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        if (hosts.Count == 0 || await this.IsAdministratorAsync(cancellationToken).ConfigureAwait(false))
        {
            return hosts;
        }

        // 一次查出该用户的全部分配，而不是逐台主机问一次
        var assignments = await _assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        var visibleHostIds = new HashSet<Guid>();

        foreach (var assignment in assignments)
        {
            var role = await _roles.QueryByIdAsync(assignment.RoleId, cancellationToken).ConfigureAwait(false);
            if (role is null)
            {
                continue;
            }

            var granted = await _roles.GetPermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false);

            if (!granted.Contains(Permission.HostRead, StringComparer.Ordinal))
            {
                continue;
            }

            // 全局分配让它在所有主机上都可见；主机分配只让它在那台主机上可见
            if (assignment.IsGlobal)
            {
                visibleHostIds.UnionWith(hosts.Select(host => host.Id));
            }
            else
            {
                visibleHostIds.Add(assignment.HostId!.Value);
            }
        }

        return [.. hosts.Where(host => visibleHostIds.Contains(host.Id))];
    }

    /// <inheritdoc />
    public async Task<bool> IsAdministratorAsync(CancellationToken cancellationToken = default)
    {
        if (_isAdministrator is { } cached)
        {
            return cached;
        }

        // 角色只能来自当前请求上下文（令牌），而主体来自参数——
        // 两者在调用点一致，但角色判定不该依赖「参数恰好等于当前用户」。
        var userId = _currentUser.UserId ?? Guid.Empty;

        if (userId == Guid.Empty)
        {
            _isAdministrator = false;
            return false;
        }

        var adminRole = await _roles.FindByNameAsync(Role.AdminName, cancellationToken).ConfigureAwait(false);

        // 只认全局分配：主机分配里的 admin 只带来该主机的 Host 域权限，不是系统管理员。
        // 令牌里的 role claim 只做一次快速否决——claim 说不是，就不必再查库。
        if (adminRole is null ||
            !_currentUser.Roles.Contains(Role.AdminName, StringComparer.OrdinalIgnoreCase))
        {
            _isAdministrator = false;
            return false;
        }

        var globalCount = await _assignments
            .CountGlobalByUserAndRoleAsync(userId, adminRole.Id, cancellationToken)
            .ConfigureAwait(false);

        _isAdministrator = globalCount > 0;
        return _isAdministrator.Value;
    }
}
