using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;

namespace Whalefleet.Infrastructure.Permissions;

/// <summary>请求内复用分配与角色读取；容器分配永不扩大主机授权。</summary>
internal sealed class ContainerPermissionChecker(
    IPermissionChecker hostPermissions,
    IUserRoleAssignmentRepository assignments,
    IRoleRepository roles,
    IAuthorizationSettingsStore settings,
    ITeamPermissionResolver teamCeiling) : IContainerPermissionChecker
{
    private readonly Dictionary<Guid, IReadOnlyList<UserRoleAssignment>> _assignments = [];
    private readonly Dictionary<Guid, IReadOnlySet<string>> _roles = [];
    private readonly Dictionary<(Guid User, Guid Host, string Container), IReadOnlySet<string>> _permissions = [];
    private string? _mode;
    private Guid? _adminRoleId;
    private bool _adminRoleLoaded;

    /// <inheritdoc />
    public async Task<bool> IsAdministratorAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await this.IsAdminAsync(await this.GetAssignmentsAsync(userId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, Guid hostId, string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        var key = (userId, hostId, containerId);
        if (_permissions.TryGetValue(key, out var cached)) return cached;
        var upper = await hostPermissions.GetEffectivePermissionsAsync(userId, hostId, cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(upper.Where(p => p is Permission.ContainerRead or Permission.ContainerWrite), StringComparer.Ordinal);
        var rows = await this.GetAssignmentsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (await this.IsAdminAsync(rows, cancellationToken).ConfigureAwait(false))
        {
            result.UnionWith([Permission.ContainerRead, Permission.ContainerWrite]);
        }
        else
        {
            var matching = rows.Where(a => a.HostId == hostId && a.ContainerId == containerId).ToArray();
            if (matching.Length == 0)
            {
                if (await this.GetModeAsync(cancellationToken).ConfigureAwait(false) == "requireAssignment") result.Clear();
            }
            else
            {
                var allowed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in matching) allowed.UnionWith(await this.GetRoleAsync(row.RoleId, cancellationToken).ConfigureAwait(false));
                result.IntersectWith(allowed);
            }
        }

        // 0.13.0：团队权限上限同样作用在容器级。
        // 上限为 null（该容器没有团队约束，或用户是内置全局管理员）时不收窄。
        var ceiling = await teamCeiling
            .GetCeilingAsync(userId, hostId, containerId, cancellationToken)
            .ConfigureAwait(false);

        if (ceiling is not null)
        {
            result.IntersectWith(ceiling);
        }

        _permissions[key] = result;
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsRestrictedAsync(Guid userId, Guid hostId, CancellationToken cancellationToken = default)
    {
        var rows = await this.GetAssignmentsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (await this.IsAdminAsync(rows, cancellationToken).ConfigureAwait(false)) return false;
        if (await this.GetModeAsync(cancellationToken).ConfigureAwait(false) == "requireAssignment") return true;
        var upper = await hostPermissions.GetEffectivePermissionsAsync(userId, hostId, cancellationToken).ConfigureAwait(false);
        foreach (var id in rows.Where(a => a.HostId == hostId && a.ContainerId is not null).Select(a => a.ContainerId!).Distinct(StringComparer.Ordinal))
        {
            var effective = await this.GetPermissionsAsync(userId, hostId, id, cancellationToken).ConfigureAwait(false);
            if (upper.Any(p => (p is Permission.ContainerRead or Permission.ContainerWrite) && !effective.Contains(p))) return true;
        }
        return false;
    }

    private async Task<IReadOnlyList<UserRoleAssignment>> GetAssignmentsAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_assignments.TryGetValue(userId, out var rows))
        {
            rows = await assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);
            _assignments[userId] = rows;
        }
        return rows;
    }

    private async Task<IReadOnlySet<string>> GetRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        if (!_roles.TryGetValue(roleId, out var permissions))
        {
            permissions = new HashSet<string>(await roles.GetPermissionsAsync(roleId, cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
            _roles[roleId] = permissions;
        }
        return permissions;
    }

    private async Task<bool> IsAdminAsync(IReadOnlyList<UserRoleAssignment> rows, CancellationToken cancellationToken)
    {
        if (!_adminRoleLoaded)
        {
            _adminRoleId = (await roles.FindByNameAsync(Role.AdminName, cancellationToken).ConfigureAwait(false))?.Id;
            _adminRoleLoaded = true;
        }
        return _adminRoleId is { } adminId && rows.Any(a => a.IsGlobal && a.RoleId == adminId);
    }

    private async Task<string> GetModeAsync(CancellationToken cancellationToken) =>
        _mode ??= await settings.GetModeAsync(cancellationToken).ConfigureAwait(false);
}
