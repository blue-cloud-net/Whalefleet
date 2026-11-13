using System.Collections.Frozen;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Teams;
using Whalefleet.Core.Users;

namespace Whalefleet.Infrastructure.Permissions;

/// <summary>
/// 团队权限上限解析器（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>算法</b>：用户 → 所属团队 → 各团队在目标作用域上的角色授权 → 这些角色的权限并集。</para>
/// <para><b>并集在前，交集在后</b>：多团队时先并集各团队的权限，再与个人权限取交集。
/// 反过来（先交集）会把多团队协作变成「必须同时满足所有团队」，
/// 那极易把人锁死——而「加进团队」本身就已经是一种收窄了。</para>
/// <para><b>内置全局管理员豁免</b>：<c>admin</c> 的全局分配不受团队上限约束。
/// 否则把管理员误加进一个受限团队就会把他锁在门外——而团队管理权限
/// （<c>team.write</c>）比系统管理员低得多，误操作完全可能发生。</para>
/// <para><b>缓存是请求级的</b>：本类是 Scoped，一次请求内用惰性求值把结果钉住。
/// 团队变更在<b>下一次</b>请求生效——与角色变更的生效口径一致。</para>
/// <para><b>为什么在 Infrastructure</b>：它是<b>仓储之上的只读适配器</b>
/// （与 <see cref="PermissionChecker"/> 同层），不包含任何业务编排。</para>
/// </remarks>
internal sealed class TeamPermissionResolver : ITeamPermissionResolver
{
    private readonly ITeamMemberRepository _members;
    private readonly ITeamRoleAssignmentRepository _teamAssignments;
    private readonly IRoleRepository _roles;
    private readonly IUserRoleAssignmentRepository _userAssignments;
    private readonly ILogger<TeamPermissionResolver> _logger;

    /// <summary>
    /// 请求内缓存：键为 (用户, 主机, 容器)，值为上限集合（null = 无约束）。
    /// </summary>
    private readonly Dictionary<(Guid UserId, Guid? HostId, string? ContainerId), IReadOnlySet<string>?> _cache = [];

    /// <summary>
    /// 内置 admin 角色主键（懒加载）。
    /// </summary>
    private Guid? _adminRoleId;

    /// <summary>
    /// 初始化解析器。
    /// </summary>
    /// <param name="members">团队成员仓储。</param>
    /// <param name="teamAssignments">团队角色授权仓储。</param>
    /// <param name="roles">角色仓储。</param>
    /// <param name="userAssignments">用户角色分配仓储（判定内置 admin）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public TeamPermissionResolver(
        ITeamMemberRepository members,
        ITeamRoleAssignmentRepository teamAssignments,
        IRoleRepository roles,
        IUserRoleAssignmentRepository userAssignments,
        ILogger<TeamPermissionResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(teamAssignments);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(userAssignments);
        ArgumentNullException.ThrowIfNull(logger);

        _members = members;
        _teamAssignments = teamAssignments;
        _roles = roles;
        _userAssignments = userAssignments;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>?> GetCeilingAsync(
        Guid userId,
        Guid? hostId,
        string? containerId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var key = (userId, hostId, containerId);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IReadOnlySet<string>? result = null;

        if (!await this.IsGlobalAdministratorAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            var teamIds = await _members
                .ListTeamIdsByUsersAsync([userId], cancellationToken)
                .ConfigureAwait(false);

            if (teamIds.Count > 0)
            {
                var assignments = await _teamAssignments
                    .ListByTeamsAsync(teamIds.Distinct().ToArray(), cancellationToken)
                    .ConfigureAwait(false);

                var scoped = assignments
                    .Where(assignment => AppliesTo(assignment, hostId, containerId))
                    .ToArray();

                if (scoped.Length > 0)
                {
                    result = await this.BuildCeilingAsync(scoped, hostId, containerId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        _cache[key] = result;
        return result;
    }

    /// <summary>
    /// 判断一条团队授权是否在目标作用域上生效。
    /// </summary>
    /// <param name="assignment">团队授权记录。</param>
    /// <param name="hostId">作用域主机；<see langword="null"/> 表示全局。</param>
    /// <param name="containerId">容器完整 ID；<see langword="null"/> 表示非容器级。</param>
    /// <returns>生效返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 规则与个人授权一致：
    /// ① 容器级作用域只认<b>该容器</b>的授权；
    /// ② 主机级作用域认「全局授权 + 该主机的授权」（不含容器级）；
    /// ③ 全局作用域只认全局授权。
    /// </remarks>
    private static bool AppliesTo(TeamRoleAssignment assignment, Guid? hostId, string? containerId)
    {
        if (containerId is not null)
        {
            return assignment.ContainerId == containerId && assignment.HostId == hostId;
        }

        if (assignment.ContainerId is not null)
        {
            return false;
        }

        if (hostId is null)
        {
            return assignment.HostId is null;
        }

        return assignment.HostId is null || assignment.HostId == hostId;
    }

    /// <summary>
    /// 汇总这些团队角色的权限并集（按作用域过滤）。
    /// </summary>
    /// <param name="assignments">生效的团队授权。</param>
    /// <param name="hostId">作用域主机。</param>
    /// <param name="containerId">容器完整 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限并集。</returns>
    private async Task<IReadOnlySet<string>> BuildCeilingAsync(
        IReadOnlyList<TeamRoleAssignment> assignments,
        Guid? hostId,
        string? containerId,
        CancellationToken cancellationToken)
    {
        var roleIds = assignments.Select(assignment => assignment.RoleId).Distinct().ToArray();
        var roles = await _roles.QueryListByIdsAsync(roleIds, cancellationToken).ConfigureAwait(false);

        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            var granted = await _roles.GetPermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false);

            foreach (var permission in granted)
            {
                if (InScope(permission, hostId, containerId))
                {
                    permissions.Add(permission);
                }
            }
        }

        return permissions.Count == 0
            ? FrozenSet<string>.Empty
            : permissions.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// 判断某项权限是否属于目标作用域。
    /// </summary>
    /// <param name="permission">权限标识。</param>
    /// <param name="hostId">作用域主机。</param>
    /// <param name="containerId">容器完整 ID。</param>
    /// <returns>属于该作用域返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 与个人授权的口径一致：全局作用域只认 Global 权限；
    /// 主机作用域认 Host 权限（其中含 container.read / container.write）；
    /// 容器作用域只认<b>容器级</b>的那两项。
    /// </remarks>
    private static bool InScope(string permission, Guid? hostId, string? containerId)
    {
        if (containerId is not null)
        {
            return permission is Permission.ContainerRead or Permission.ContainerWrite;
        }

        var scope = Permission.ScopeOf(permission);

        return hostId is null
            ? scope == PermissionScope.Global
            : scope == PermissionScope.Host;
    }

    /// <summary>
    /// 判断用户是否持有内置 admin 角色的<b>全局</b>分配。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是全局管理员返回 <see langword="true"/>。</returns>
    private async Task<bool> IsGlobalAdministratorAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_adminRoleId is null)
        {
            _adminRoleId = (await _roles.FindByNameAsync(Role.AdminName, cancellationToken).ConfigureAwait(false))?.Id;

            if (_adminRoleId is null)
            {
                // 角色被删了（不该发生）：此时没有 admin，也就没有豁免。
                return false;
            }
        }

        var assignments = await _userAssignments
            .ListByUserAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return assignments.Any(assignment => assignment.IsGlobal && assignment.RoleId == _adminRoleId.Value);
    }
}
