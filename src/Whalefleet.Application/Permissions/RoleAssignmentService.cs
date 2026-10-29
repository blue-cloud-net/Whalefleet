using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Permissions;

/// <summary>
/// 角色分配用例（列出 / 分配 / 撤销），分主机中心与用户中心两个视角，
/// 对应 <c>/api/v1/admin/hosts/{hostId}/roles/*</c> 与 <c>/api/v1/admin/users/{userId}/roles/*</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么是两条视角而不是一条</b>：它们回答的是两个不同的问题——
/// 「谁能访问这台机器」与「这个人拥有什么」。合并成一个带可空 <c>hostId</c> 的端点，
/// 意味着前端要么为了显示主机名多查一次，要么拿不到主机名。</para>
/// <para><b>分配是幂等的 upsert</b>：对已有 (user, role, host) 组合再次分配返回<b>同一条</b>记录，
/// 而不是插入第二条。这条在数据库上有部分唯一索引兜底，但服务层仍先查一次，
/// 以便在已存在时<b>不重新分配新主键</b>——否则界面刷新后会看到一条 id 不同的新记录。</para>
/// <para><b>错误码</b>：用户不存在 <c>1201</c>、主机不存在 <c>1300</c>、角色不存在 <c>1510</c>、
/// 分配不存在 <c>1515</c>、参数非法 <c>1000</c>。</para>
/// <para><b>日志纪律</b>：只记用户主键、用户名、角色名与主机名。</para>
/// </remarks>
public sealed class RoleAssignmentService
{
    private readonly IUserRoleAssignmentRepository _assignments;
    private readonly IRoleRepository _roles;
    private readonly IUserRepository _users;
    private readonly IHostRepository _hosts;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RoleAssignmentService> _logger;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    /// <param name="assignments">分配仓储。</param>
    /// <param name="roles">角色仓储。</param>
    /// <param name="users">用户仓储（校验目标用户存在并取用户名）。</param>
    /// <param name="hosts">主机仓储（按名称解析作用域主机）。</param>
    /// <param name="audit">审计日志（授权变更是安全事件，必须留痕）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public RoleAssignmentService(
        IUserRoleAssignmentRepository assignments,
        IRoleRepository roles,
        IUserRepository users,
        IHostRepository hosts,
        IAuditLogger audit,
        ILogger<RoleAssignmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _assignments = assignments;
        _roles = roles;
        _users = users;
        _hosts = hosts;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// 列出某台主机上的分配（不含全局分配）。
    /// </summary>
    /// <param name="hostName">主机标识（主机名）。</param>
    /// <param name="request">查询请求（可指定单个用户）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配列表；无分配时返回<b>空列表</b>。</returns>
    /// <exception cref="BusinessException">
    /// 主机名无效（<c>1000</c>）、主机不存在（<c>1300</c>）或指定的用户不存在（<c>1201</c>）。
    /// </exception>
    public async Task<IReadOnlyList<RoleAssignmentSummary>> ListByHostAsync(
        string hostName,
        ListHostRoleAssignmentsRequest? request,
        CancellationToken cancellationToken = default)
    {
        var host = await this.RequireHostAsync(hostName, cancellationToken).ConfigureAwait(false);

        var rows = await _assignments.ListByHostAsync(host.Id, cancellationToken).ConfigureAwait(false);

        if (request?.UserId is { } userId)
        {
            // 先确认用户存在再过滤：给一个不存在的 userId 返回空列表，
            // 会让界面把「这个人没有分配」和「查错了人」渲染成同一个空态
            await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);
            rows = [.. rows.Where(row => row.UserId == userId)];
        }

        return await this.DecorateAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 列出某个用户的全部分配（全局 + 各主机）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配列表；无分配时返回<b>空列表</b>。</returns>
    /// <exception cref="BusinessException">用户不存在（<c>1201</c>）。</exception>
    public async Task<IReadOnlyList<RoleAssignmentSummary>> ListByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);

        var rows = await _assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return await this.DecorateAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 分配一个角色给用户（全局或某台主机）。
    /// </summary>
    /// <param name="request">分配请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分配记录（已存在时返回原有记录）。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、用户不存在（<c>1201</c>）、主机不存在（<c>1300</c>）
    /// 或角色不存在（<c>1510</c>）。
    /// </exception>
    public async Task<RoleAssignmentSummary> AssignAsync(
        AssignRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequireUserId(request.UserId);
        var user = await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var role = await ResolveRoleAsync(request, cancellationToken).ConfigureAwait(false);

        DockerHost? host = null;
        if (!String.IsNullOrWhiteSpace(request.Host))
        {
            host = await this.RequireHostAsync(request.Host, cancellationToken).ConfigureAwait(false);
        }

        var hostId = host?.Id;

        // 先查再决定是插入还是复用：分配是幂等的 upsert，
        // 重复分配不该换一个主键——否则界面刷新后会看到一条 id 不同的"新"记录
        var existing = await _assignments
            .GetAsync(userId, role.Id, hostId, cancellationToken)
            .ConfigureAwait(false);

        UserRoleAssignment assignment;
        if (existing is null)
        {
            assignment = UserRoleAssignment.Assign(Guid.NewGuid(), userId, role.Id, hostId);
            await _assignments.AddAsync(assignment, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            assignment = existing;
        }

        _logger.LogInformation(
            "已为用户 {UserId}（{Username}）分配角色 {Role}（作用域：{Scope}）",
            userId,
            user.Username,
            role.Name,
            host?.Name ?? "全局");

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.RoleAssign,
                Outcome = AuditOutcome.Success,
                HostId = hostId,
                HostName = host?.Name,
                TargetType = AuditTargetTypes.RoleAssignment,
                TargetId = assignment.Id.ToString("D"),
                TargetName = $"{user.Username} → {role.Name}",
                Detail = $"作用域={host?.Name ?? "全局"}",
            },
            cancellationToken).ConfigureAwait(false);

        return new RoleAssignmentSummary(
            assignment.Id,
            user.Id,
            user.Username,
            role.Id,
            role.Name,
            role.DisplayName,
            hostId,
            host?.Name,
            assignment.LastModificationTime);
    }

    /// <summary>
    /// 撤销一条分配（软删除）。
    /// </summary>
    /// <param name="request">撤销请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、角色不存在（<c>1510</c>）或该分配不存在（<c>1515</c>）。
    /// </exception>
    public async Task RevokeAsync(
        RevokeRoleAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequireUserId(request.UserId);
        var role = await ResolveRoleAsync(request, cancellationToken).ConfigureAwait(false);

        Guid? hostId = null;
        if (!String.IsNullOrWhiteSpace(request.Host))
        {
            var host = await this.RequireHostAsync(request.Host, cancellationToken).ConfigureAwait(false);
            hostId = host.Id;
        }

        // 撤销走软删除而不是物理删除：保留「谁在什么时候撤的」这条事实
        var existing = await _assignments
            .GetAsync(userId, role.Id, hostId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BusinessException(
                ErrorCodes.RoleAssignmentNotFound,
                "该用户在此作用域上没有这个角色的分配");

        if (!await _assignments.RemoveByIdAsync(existing.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.RoleAssignmentNotFound, "该分配不存在");
        }

        _logger.LogInformation(
            "已撤销用户 {UserId} 的角色 {Role}（作用域：{Scope}）",
            userId,
            role.Name,
            request.Host?.Trim() ?? "全局");

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.RoleRevoke,
                Outcome = AuditOutcome.Success,
                HostId = hostId,
                HostName = request.Host?.Trim(),
                TargetType = AuditTargetTypes.RoleAssignment,
                TargetId = existing.Id.ToString("D"),
                TargetName = $"{userId:D} → {role.Name}",
                Detail = $"作用域={request.Host?.Trim() ?? "全局"}",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析角色：优先按主键，其次按标识。
    /// </summary>
    /// <param name="request">含 <c>roleId</c> / <c>role</c> 的请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色。</returns>
    /// <exception cref="BusinessException">未给出定位条件（<c>1000</c>）或角色不存在（<c>1510</c>）。</exception>
    private async Task<Role> ResolveRoleAsync(
        RoleSelectorRequest request,
        CancellationToken cancellationToken)
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

    private Task<Role> ResolveRoleAsync(AssignRoleRequest request, CancellationToken cancellationToken) =>
        this.ResolveRoleAsync(
            new RoleSelectorRequest { RoleId = request.RoleId, Role = request.Role },
            cancellationToken);

    private Task<Role> ResolveRoleAsync(RevokeRoleAssignmentRequest request, CancellationToken cancellationToken) =>
        this.ResolveRoleAsync(
            new RoleSelectorRequest { RoleId = request.RoleId, Role = request.Role },
            cancellationToken);

    /// <summary>
    /// 批量补齐展示字段（用户名 / 角色名 / 主机名）。
    /// </summary>
    /// <remarks>
    /// <b>为什么一次性取全再在内存里映射</b>：逐行查库会把一个分配列表变成 3N 次往返；
    /// 而这三条查询的输入都是这个列表本身，一次批量查完再拼装即可。
    /// 查不到的（已软删除 / 不存在）不报错而是留空串——分配记录本身仍然要显示出来，
    /// 否则管理员会看到「列表里少了一个人」却不知道为什么。
    /// </remarks>
    private async Task<IReadOnlyList<RoleAssignmentSummary>> DecorateAsync(
        IReadOnlyList<UserRoleAssignment> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var roleIds = rows.Select(row => row.RoleId).Distinct().ToArray();
        var userIds = rows.Select(row => row.UserId).Distinct().ToArray();
        var hostIds = rows
            .Where(row => row.HostId is not null)
            .Select(row => row.HostId!.Value)
            .Distinct()
            .ToArray();

        var roles = (await _roles.QueryListByIdsAsync(roleIds, cancellationToken).ConfigureAwait(false))
            .ToDictionary(role => role.Id);
        var users = (await _users.QueryListByIdsAsync(userIds, cancellationToken).ConfigureAwait(false))
            .ToDictionary(user => user.Id);
        var hosts = hostIds.Length == 0
            ? []
            : (await _hosts.QueryListByIdsAsync(hostIds, cancellationToken).ConfigureAwait(false))
                .ToDictionary(host => host.Id);

        var summaries = new List<RoleAssignmentSummary>(rows.Count);

        foreach (var row in rows)
        {
            var role = roles.GetValueOrDefault(row.RoleId);
            var host = row.HostId is { } id ? hosts.GetValueOrDefault(id) : null;

            summaries.Add(new RoleAssignmentSummary(
                row.Id,
                row.UserId,
                users.GetValueOrDefault(row.UserId)?.Username ?? String.Empty,
                row.RoleId,
                role?.Name ?? String.Empty,
                role?.DisplayName,
                row.HostId,
                host?.Name,
                row.LastModificationTime));
        }

        return summaries;
    }

    private async Task<User> RequireUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _users.QueryByIdAsync(userId, cancellationToken).ConfigureAwait(false)
        ?? throw new BusinessException(ErrorCodes.UserNotFound, "用户不存在");

    private async Task<DockerHost> RequireHostAsync(string hostName, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(hostName))
        {
            throw BusinessException.Validation("hostId 不能为空");
        }

        var name = hostName.Trim();
        return await _hosts.FindByNameAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机 '{name}' 不存在");
    }

    private static Guid RequireUserId(Guid? userId) =>
        userId is { } id && id != Guid.Empty
            ? id
            : throw BusinessException.Validation("userId 不能为空");
}
