using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Permissions;

/// <summary>
/// 角色管理用例（列表 / 创建 / 改信息 / 配权限 / 设默认 / 删除），
/// 对应 <c>/api/v1/admin/roles/*</c>。
/// </summary>
/// <remarks>
/// <para><b>本层不做管理员判定</b>：调用这些端点必定是管理员，由 Api 层的
/// 权限策略（<c>role.write</c> / <c>role.read</c>）把关。与 <c>Users.UserService</c> 不同，
/// 这里不需要在服务层再判一次。</para>
/// <para><b>跨行规则在服务层，实体只管自身不变量</b>：
/// 「角色标识是否已被占用」「这个角色还在被谁用」「谁是最后一名管理员」都要查库，
/// 因此由本类判定；<c>Role.Retire()</c> 只管「内置角色不可删」。</para>
/// <para><b>错误码</b>：角色不存在 <c>1510</c>、角色名已占用 <c>1511</c>、角色仍有分配 <c>1512</c>、
/// 内置角色受保护 <c>1513</c>、未知权限 <c>1514</c>、参数非法 <c>1000</c>。</para>
/// <para><b>日志纪律</b>：只记角色标识与权限数量，不记任何凭据。</para>
/// </remarks>
public sealed class RoleService
{
    private readonly IRoleRepository _roles;
    private readonly IUserRoleAssignmentRepository _assignments;
    private readonly ILogger<RoleService> _logger;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    /// <param name="roles">角色仓储。</param>
    /// <param name="assignments">分配仓储（删除前检查角色是否仍在用）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public RoleService(
        IRoleRepository roles,
        IUserRoleAssignmentRepository assignments,
        ILogger<RoleService> logger)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(logger);

        _roles = roles;
        _assignments = assignments;
        _logger = logger;
    }

    /// <summary>
    /// 列出角色（含权限集与当前分配数）。
    /// </summary>
    /// <param name="request">查询请求（可只看自定义角色）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色列表；无角色时返回<b>空列表</b>。</returns>
    public async Task<IReadOnlyList<RoleSummary>> ListAsync(
        ListRolesRequest? request,
        CancellationToken cancellationToken = default)
    {
        var roles = await _roles.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new List<RoleSummary>(roles.Count);

        foreach (var role in roles)
        {
            if (request?.CustomOnly == true && role.IsBuiltIn)
            {
                continue;
            }

            summaries.Add(await this.SummarizeAsync(role, cancellationToken).ConfigureAwait(false));
        }

        return summaries;
    }

    /// <summary>
    /// 返回权限目录（前端权限矩阵的列定义）。
    /// </summary>
    /// <returns>权限目录，按「主机域在前、全局在后」排列。</returns>
    public static IReadOnlyList<PermissionDefinition> ListPermissionDefinitions() =>
    [
        .. Permission.Hosts.Select(Describe),
        .. Permission.Globals.Select(Describe),
    ];

    /// <summary>
    /// 创建一个自定义角色。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的角色。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>，含未知权限名 <c>1514</c>）或角色名已占用（<c>1511</c>）。
    /// </exception>
    public async Task<RoleSummary> CreateAsync(
        CreateRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (String.IsNullOrWhiteSpace(request.Name))
        {
            throw BusinessException.Validation("name 不能为空");
        }

        if (await _roles.ExistsByNameAsync(request.Name, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.RoleNameAlreadyExists, $"角色 '{request.Name}' 已存在");
        }

        // 校验在写库之前完成：非法权限名不允许在库里留下一条「待修正」的角色
        var permissions = PermissionNames.Normalize(request.Permissions, "permissions", allowEmpty: true);

        var role = Role.Create(
            Guid.NewGuid(),
            request.Name,
            request.DisplayName,
            request.Description,
            permissions);

        await _roles.AddAsync(role, cancellationToken).ConfigureAwait(false);

        if (permissions.Count > 0)
        {
            await _roles.ReplacePermissionsAsync(role.Id, permissions, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "已创建角色 {Role}（权限 {Count} 项）",
            role.Name,
            permissions.Count);

        return Summarize(role, permissions, 0);
    }

    /// <summary>
    /// 修改角色的可读信息（标识与权限不在此端点）。
    /// </summary>
    /// <param name="update">定位（<c>roleId</c> 或 <c>role</c>）与要修改的内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>修改后的角色。</returns>
    /// <exception cref="BusinessException">参数非法（<c>1000</c>）或角色不存在（<c>1510</c>）。</exception>
    public async Task<RoleSummary> UpdateAsync(
        UpdateRoleRequest update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var role = await this.ResolveAsync(update, cancellationToken).ConfigureAwait(false);

        role.ChangeDescription(update.DisplayName, update.Description);
        await _roles.UpdateAsync(role, cancellationToken).ConfigureAwait(false);

        var permissions = await _roles.GetPermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false);
        var count = await _assignments.CountByRoleIdAsync(role.Id, cancellationToken).ConfigureAwait(false);

        return Summarize(role, permissions, count);
    }

    /// <summary>
    /// 覆盖式设置自定义角色的权限集。
    /// </summary>
    /// <param name="body">定位（<c>roleId</c> 或 <c>role</c>）与新的权限集（允许空数组）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>修改后的角色。</returns>
    /// <exception cref="BusinessException">
    /// 角色不存在（<c>1510</c>）、内置角色受保护（<c>1513</c>）或未知权限（<c>1514</c>）。
    /// </exception>
    public async Task<RoleSummary> SetPermissionsAsync(
        SetRolePermissionsRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var role = await this.ResolveAsync(body, cancellationToken).ConfigureAwait(false);

        if (role.IsBuiltIn)
        {
            throw new BusinessException(
                ErrorCodes.RoleProtected,
                $"内置角色 '{role.Name}' 的权限集不可修改");
        }

        var permissions = PermissionNames.Normalize(body.Permissions, "permissions", allowEmpty: true);

        role.SetPermissions(permissions);
        await _roles.UpdateAsync(role, cancellationToken).ConfigureAwait(false);
        await _roles.ReplacePermissionsAsync(role.Id, permissions, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("已更新角色 {Role} 的权限集（{Count} 项）", role.Name, permissions.Count);

        var count = await _assignments.CountByRoleIdAsync(role.Id, cancellationToken).ConfigureAwait(false);

        return Summarize(role, permissions, count);
    }

    /// <summary>
    /// 把某个角色设为默认角色（创建用户时采用）。
    /// </summary>
    /// <param name="request">定位请求（角色主键或标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>设为默认后的角色。</returns>
    /// <exception cref="BusinessException">角色不存在（<c>1510</c>）。</exception>
    public async Task<RoleSummary> SetDefaultAsync(
        RoleSelectorRequest request,
        CancellationToken cancellationToken = default)
    {
        var role = await this.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        if (!await _roles.SetDefaultAsync(role.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.RoleNotFound, $"角色 '{role.Name}' 不存在");
        }

        var permissions = await _roles.GetPermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false);
        var count = await _assignments.CountByRoleIdAsync(role.Id, cancellationToken).ConfigureAwait(false);

        return Summarize(role, permissions, count);
    }

    /// <summary>
    /// 删除自定义角色。仍有有效分配时拒绝。
    /// </summary>
    /// <param name="request">定位请求（角色主键或标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 角色不存在（<c>1510</c>）、内置角色受保护（<c>1513</c>）或角色仍有分配（<c>1512</c>）。
    /// </exception>
    public async Task RemoveAsync(
        RoleSelectorRequest request,
        CancellationToken cancellationToken = default)
    {
        var role = await this.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        if (role.IsBuiltIn)
        {
            throw new BusinessException(
                ErrorCodes.RoleProtected,
                $"内置角色 '{role.Name}' 不可删除");
        }

        // 先看有没有人在用：级联撤销会悄悄收回没人要求收回的访问权
        var inUse = await _assignments.CountByRoleIdAsync(role.Id, cancellationToken).ConfigureAwait(false);
        if (inUse > 0)
        {
            throw new BusinessException(
                ErrorCodes.RoleInUse,
                $"角色 '{role.Name}' 仍有 {inUse} 条有效分配，请先撤销分配");
        }

        if (!await _roles.RemoveByIdAsync(role.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.RoleNotFound, $"角色 '{role.Name}' 不存在");
        }

        _logger.LogInformation("已删除角色 {Role}", role.Name);
    }

    /// <summary>
    /// 按主键或标识解析角色。
    /// </summary>
    /// <param name="request">定位请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色。</returns>
    /// <exception cref="BusinessException">未给出定位条件（<c>1000</c>）或角色不存在（<c>1510</c>）。</exception>
    internal async Task<Role> ResolveAsync(RoleSelectorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RoleId is { } roleId && roleId != Guid.Empty)
        {
            return await _roles.QueryByIdAsync(roleId, cancellationToken).ConfigureAwait(false)
                ?? throw new BusinessException(ErrorCodes.RoleNotFound, "角色不存在");
        }

        if (!String.IsNullOrWhiteSpace(request.Role))
        {
            var name = request.Role.Trim();
            return await _roles.FindByNameAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new BusinessException(ErrorCodes.RoleNotFound, $"角色 '{name}' 不存在");
        }

        throw BusinessException.Validation("roleId 与 role 至少提供一个");
    }

    private async Task<RoleSummary> SummarizeAsync(Role role, CancellationToken cancellationToken)
    {
        var permissions = await _roles.GetPermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false);
        var count = await _assignments.CountByRoleIdAsync(role.Id, cancellationToken).ConfigureAwait(false);

        return Summarize(role, permissions, count);
    }

    private static RoleSummary Summarize(Role role, IReadOnlyList<string> permissions, int assignmentCount) =>
        new(
            role.Id,
            role.Name,
            role.DisplayName,
            role.Description,
            role.IsBuiltIn,
            role.IsDefault,
            permissions,
            assignmentCount);

    private static PermissionDefinition Describe(string permission)
    {
        var separator = permission.IndexOf('.', StringComparison.Ordinal);
        var domain = permission[..separator];
        var level = permission[(separator + 1)..];

        return new PermissionDefinition(
            permission,
            domain,
            level,
            ScopeName(Permission.ScopeOf(permission)));
    }

    /// <summary>
    /// 作用域 → 对外的小写名字。
    /// </summary>
    /// <remarks>
    /// 刻意不做 <c>ToString().ToLowerInvariant()</c>：那样会把枚举成员名的变更
    /// 直接变成 API 契约的破坏，而这里显式列举意味着改名一定会先撞到编译错误。
    /// </remarks>
    private static string ScopeName(PermissionScope scope) => scope switch
    {
        PermissionScope.Global => "global",
        PermissionScope.Host => "host",
        PermissionScope.Container => "container",
        _ => "global",
    };
}
