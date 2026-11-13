using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Teams;
using Whalefleet.Core.Users;

namespace Whalefleet.Application.Teams;

/// <summary>
/// 团队管理用例（花名册 + 成员 + 权限上限 + 显式同步）。
/// </summary>
/// <remarks>
/// <para><b>团队只收窄、不扩权</b>：给团队授角色只限定成员在已授权作用域内的权限上限；
/// 成员的个人授权仍写在 <c>user_role_assignments</c>，由「同步到成员」显式写入。
/// 详见 <see cref="Team"/> 与 <c>TeamPermissionResolver</c>。</para>
/// <para><b>加入团队不会自动获得权限</b>：这是刻意的。团队成员变更往往由非管理员操作，
/// 若自动授予，那会变成一条绕过 <c>user.write</c> 的提权路径。
/// 界面上必须把这句话说出来，而不是让人以为加进团队就有权限了。</para>
/// <para><b>幂等</b>：重复授予团队角色、重复加成员都返回同一条记录/同一个状态，
/// 不产生第二条。</para>
/// </remarks>
public sealed class TeamService
{
    /// <summary>列表默认每页条数。</summary>
    public const int DefaultPageSize = 20;

    /// <summary>列表每页条数上限。</summary>
    public const int MaxPageSize = 100;

    private readonly ITeamRepository _teams;
    private readonly ITeamMemberRepository _members;
    private readonly ITeamRoleAssignmentRepository _assignments;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IHostRepository _hosts;
    private readonly IUserRoleAssignmentRepository _userAssignments;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TeamService> _logger;

    /// <summary>
    /// 初始化团队管理用例。
    /// </summary>
    /// <param name="teams">团队仓储。</param>
    /// <param name="members">团队成员仓储。</param>
    /// <param name="assignments">团队角色授权仓储。</param>
    /// <param name="users">用户仓储。</param>
    /// <param name="roles">角色仓储。</param>
    /// <param name="hosts">主机仓储（按名称解析作用域主机）。</param>
    /// <param name="userAssignments">个人角色分配仓储（同步时写入）。</param>
    /// <param name="audit">审计日志。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public TeamService(
        ITeamRepository teams,
        ITeamMemberRepository members,
        ITeamRoleAssignmentRepository assignments,
        IUserRepository users,
        IRoleRepository roles,
        IHostRepository hosts,
        IUserRoleAssignmentRepository userAssignments,
        IAuditLogger audit,
        ILogger<TeamService> logger)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(userAssignments);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _teams = teams;
        _members = members;
        _assignments = assignments;
        _users = users;
        _roles = roles;
        _hosts = hosts;
        _userAssignments = userAssignments;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// 分页列出团队。
    /// </summary>
    /// <param name="request">分页参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分页结果。</returns>
    public async Task<TeamListResult> ListAsync(
        ListTeamsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pageIndex = Math.Max(0, request.PageIndex ?? 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        var total = await _teams.CountAsync(cancellationToken).ConfigureAwait(false);
        var teams = await _teams.ListAsync(pageIndex * pageSize, pageSize, cancellationToken).ConfigureAwait(false);

        if (teams.Count == 0)
        {
            return new TeamListResult([], total, pageIndex, pageSize);
        }

        var teamIds = teams.Select(team => team.Id).ToArray();

        // 成员数与授权数各查一次后在内存里映射：逐个查就是 N+1。
        var memberCounts = await _members.CountMembersAsync(teamIds, cancellationToken).ConfigureAwait(false);
        var assignmentCounts = await this.CountAssignmentsAsync(teamIds, cancellationToken).ConfigureAwait(false);

        var items = teams
            .Select(team => new TeamSummary(
                team.Id,
                team.Name,
                team.DisplayName,
                team.Description,
                memberCounts.GetValueOrDefault(team.Id),
                assignmentCounts.GetValueOrDefault(team.Id),
                team.CreationTime))
            .ToList();

        return new TeamListResult(items, total, pageIndex, pageSize);
    }

    /// <summary>
    /// 读取团队详情（成员 + 角色授权）。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队详情。</returns>
    /// <exception cref="BusinessException">团队不存在（<c>1204</c>）。</exception>
    public async Task<TeamDetail> GetAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await this.RequireTeamAsync(teamId, cancellationToken).ConfigureAwait(false);

        var memberIds = await _members.ListMemberIdsAsync(teamId, cancellationToken).ConfigureAwait(false);
        var users = await _users.QueryListByIdsAsync(memberIds, cancellationToken).ConfigureAwait(false);
        var userById = users.ToDictionary(user => user.Id);

        var members = memberIds
            .Select(userId => userById.TryGetValue(userId, out var user)
                ? new TeamMemberSummary(
                    user.Id,
                    user.Username,
                    user.CreationTime,
                    user.IsDisabled,
                    user.MustChangePassword)
                : null)
            .Where(member => member is not null)
            .Select(member => member!)
            .ToList();

        var assignments = await this.DecorateAssignmentsAsync(
            await _assignments.ListByTeamAsync(teamId, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        var summary = new TeamSummary(
            team.Id,
            team.Name,
            team.DisplayName,
            team.Description,
            members.Count,
            assignments.Count,
            team.CreationTime);

        return new TeamDetail(summary, members, assignments);
    }

    /// <summary>
    /// 创建团队。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新团队。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）或团队名已占用（<c>1203</c>）。
    /// </exception>
    public async Task<TeamSummary> CreateAsync(
        CreateTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = Require(request.Name, "name");

        if (await _teams.ExistsByNameAsync(name, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.TeamNameAlreadyExists,
                $"团队名「{name}」已被使用（不区分大小写）");
        }

        var team = Team.Create(Guid.NewGuid(), name, request.DisplayName, request.Description);

        await _teams.AddAsync(team, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("已创建团队 {TeamName}", team.Name);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.TeamCreate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.Team,
                TargetId = team.Id.ToString("D"),
                TargetName = team.Name,
            },
            cancellationToken).ConfigureAwait(false);

        return new TeamSummary(
            team.Id,
            team.Name,
            team.DisplayName,
            team.Description,
            0,
            0,
            team.CreationTime);
    }

    /// <summary>
    /// 更新团队的可读名称与备注。
    /// </summary>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）或团队不存在（<c>1204</c>）。
    /// </exception>
    public async Task UpdateAsync(
        UpdateTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TeamId is not { } teamId || teamId == Guid.Empty)
        {
            throw BusinessException.Validation("teamId 不能为空");
        }

        var team = await this.RequireTeamAsync(teamId, cancellationToken).ConfigureAwait(false);

        team.Update(request.DisplayName, request.Description);
        await _teams.UpdateAsync(team, cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.TeamUpdate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.Team,
                TargetId = team.Id.ToString("D"),
                TargetName = team.Name,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除团队（软删除）。
    /// </summary>
    /// <param name="request">删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）或团队不存在（<c>1204</c>）。
    /// </exception>
    /// <remarks>
    /// <b>不回收已发放的授权</b>：团队只是权限上限的持有者，
    /// 它被删掉后成员的个人授权仍然有效——那本来就是管理员单独授予的。
    /// 「撤销角色不级联」是本项目一贯的口径，这里保持一致。
    /// </remarks>
    public async Task RemoveAsync(
        RemoveTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TeamId is not { } teamId || teamId == Guid.Empty)
        {
            throw BusinessException.Validation("teamId 不能为空");
        }

        var team = await this.RequireTeamAsync(teamId, cancellationToken).ConfigureAwait(false);

        await _teams.RemoveByIdAsync(team.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("已删除团队 {TeamName}", team.Name);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.TeamRemove,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.Team,
                TargetId = team.Id.ToString("D"),
                TargetName = team.Name,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 添加成员（<b>幂等</b>，且<b>不会</b>授予任何权限）。
    /// </summary>
    /// <param name="request">成员请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际新增的成员数。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、团队不存在（<c>1204</c>）或用户不存在（<c>1201</c>）。
    /// </exception>
    public async Task<int> AddMembersAsync(
        UpdateTeamMembersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var team = await this.RequireTeamAsync(RequireTeamId(request.TeamId), cancellationToken).ConfigureAwait(false);
        var userIds = RequireUserIds(request.UserIds);

        // 用户必须真实存在：往团队里塞一个不存在的主键，界面上会显示一个幽灵成员。
        var users = await _users.QueryListByIdsAsync(userIds, cancellationToken).ConfigureAwait(false);
        var missing = userIds.Except(users.Select(user => user.Id)).ToArray();
        if (missing.Length > 0)
        {
            throw new BusinessException(ErrorCodes.UserNotFound, $"用户不存在：{missing.Length} 个");
        }

        var added = 0;
        foreach (var userId in userIds)
        {
            if (await _members.AddAsync(team.Id, userId, cancellationToken).ConfigureAwait(false))
            {
                added++;
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("已向团队 {TeamName} 添加 {Count} 名成员", team.Name, added);

            await _audit.RecordAsync(
                new AuditLogDraft
                {
                    Category = AuditCategory.Activity,
                    Action = AuditActions.TeamMemberAdd,
                    Outcome = AuditOutcome.Success,
                    TargetType = AuditTargetTypes.Team,
                    TargetId = team.Id.ToString("D"),
                    TargetName = team.Name,
                    Detail = $"新增 {added} 名成员（**未授予任何权限**）",
                },
                cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    /// <summary>
    /// 移除成员（<b>幂等</b>）。
    /// </summary>
    /// <param name="request">成员请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际移除的成员数。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）或团队不存在（<c>1204</c>）。
    /// </exception>
    public async Task<int> RemoveMembersAsync(
        UpdateTeamMembersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var team = await this.RequireTeamAsync(RequireTeamId(request.TeamId), cancellationToken).ConfigureAwait(false);
        var userIds = RequireUserIds(request.UserIds);

        var removed = 0;
        foreach (var userId in userIds)
        {
            if (await _members.RemoveAsync(team.Id, userId, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("已从团队 {TeamName} 移除 {Count} 名成员", team.Name, removed);

            await _audit.RecordAsync(
                new AuditLogDraft
                {
                    Category = AuditCategory.Activity,
                    Action = AuditActions.TeamMemberRemove,
                    Outcome = AuditOutcome.Success,
                    TargetType = AuditTargetTypes.Team,
                    TargetId = team.Id.ToString("D"),
                    TargetName = team.Name,
                    Detail = $"移除 {removed} 名成员（其个人授权不受影响）",
                },
                cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// 给团队授予角色（<b>权限上限</b>，幂等）。
    /// </summary>
    /// <param name="request">授权请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队角色授权记录。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、团队不存在（<c>1204</c>）、角色不存在（<c>1510</c>）
    /// 或主机不存在（<c>1300</c>）。
    /// </exception>
    public async Task<TeamRoleAssignmentSummary> AssignRoleAsync(
        TeamRoleAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var team = await this.RequireTeamAsync(RequireTeamId(request.TeamId), cancellationToken).ConfigureAwait(false);
        var role = await this.ResolveRoleAsync(request, cancellationToken).ConfigureAwait(false);
        var host = await this.ResolveHostAsync(request.Host, cancellationToken).ConfigureAwait(false);

        var containerId = request.ContainerId?.Trim();
        if (!String.IsNullOrWhiteSpace(containerId) && host is null)
        {
            throw BusinessException.Validation("容器级授权必须同时指定主机");
        }

        var existing = await _assignments
            .GetAsync(team.Id, role.Id, host?.Id, containerId, cancellationToken)
            .ConfigureAwait(false);

        TeamRoleAssignment assignment;
        if (existing is null)
        {
            assignment = TeamRoleAssignment.Grant(
                Guid.NewGuid(),
                team.Id,
                role.Id,
                host?.Id,
                string.IsNullOrWhiteSpace(containerId) ? null : containerId);

            await _assignments.AddAsync(assignment, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            assignment = existing;
        }

        _logger.LogInformation(
            "已为团队 {TeamName} 授予角色 {Role}（作用域：{Scope}）",
            team.Name,
            role.Name,
            host?.Name ?? "全局");

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.TeamRoleAssign,
                Outcome = AuditOutcome.Success,
                HostId = host?.Id,
                HostName = host?.Name,
                TargetType = AuditTargetTypes.Team,
                TargetId = team.Id.ToString("D"),
                TargetName = team.Name,
                Detail = $"角色={role.Name};作用域={host?.Name ?? "全局"};容器={containerId ?? "无"}",
            },
            cancellationToken).ConfigureAwait(false);

        return new TeamRoleAssignmentSummary(
            assignment.Id,
            role.Id,
            role.Name,
            role.DisplayName,
            host?.Id,
            host?.Name,
            assignment.ContainerId);
    }

    /// <summary>
    /// 撤销团队的角色授权（软删除）。
    /// </summary>
    /// <param name="request">授权请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、团队不存在（<c>1204</c>）、角色不存在（<c>1510</c>）
    /// 或该授权不存在（<c>1516</c>）。
    /// </exception>
    public async Task RevokeRoleAsync(
        TeamRoleAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var team = await this.RequireTeamAsync(RequireTeamId(request.TeamId), cancellationToken).ConfigureAwait(false);
        var role = await this.ResolveRoleAsync(request, cancellationToken).ConfigureAwait(false);
        var host = await this.ResolveHostAsync(request.Host, cancellationToken).ConfigureAwait(false);

        var containerId = request.ContainerId?.Trim();

        var existing = await _assignments
            .GetAsync(team.Id, role.Id, host?.Id, string.IsNullOrWhiteSpace(containerId) ? null : containerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BusinessException(
                ErrorCodes.TeamRoleAssignmentNotFound,
                "该团队在此作用域上没有这个角色的授权");

        await _assignments.RemoveByIdAsync(existing.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "已撤销团队 {TeamName} 的角色 {Role}（作用域：{Scope}）",
            team.Name,
            role.Name,
            host?.Name ?? "全局");

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.TeamRoleRevoke,
                Outcome = AuditOutcome.Success,
                HostId = host?.Id,
                HostName = host?.Name,
                TargetType = AuditTargetTypes.Team,
                TargetId = team.Id.ToString("D"),
                TargetName = team.Name,
                Detail = $"角色={role.Name};作用域={host?.Name ?? "全局"}",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把团队的角色授权<b>同步</b>到成员的个人授权（显式动作，幂等）。
    /// </summary>
    /// <param name="request">同步请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>同步结果（新建条数 / 跳过条数 / 成员数）。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）或团队不存在（<c>1204</c>）。
    /// </exception>
    /// <remarks>
    /// <b>这是「团队给权限」真正落地的一步</b>：团队授权只是上限，
    /// 它必须被写进成员的 <c>user_role_assignments</c> 才成为成员<b>个人</b>的授权基线。
    /// 加入团队<b>不会</b>自动调用它。</remarks>
    public async Task<TeamSyncResult> SyncAsync(
        SyncTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var team = await this.RequireTeamAsync(RequireTeamId(request.TeamId), cancellationToken).ConfigureAwait(false);

        var teamAssignments = await _assignments
            .ListByTeamAsync(team.Id, cancellationToken)
            .ConfigureAwait(false);

        if (teamAssignments.Count == 0)
        {
            throw BusinessException.Validation("该团队还没有任何角色授权，无需同步");
        }

        var memberIds = await _members.ListMemberIdsAsync(team.Id, cancellationToken).ConfigureAwait(false);

        // 省略 userIds = 同步给全部成员（典型场景：给团队新授了一个角色）。
        var targets = request.UserIds is { Count: > 0 } specified
            ? memberIds.Intersect(specified).ToArray()
            : memberIds.ToArray();

        if (targets.Length == 0)
        {
            return new TeamSyncResult(0, 0, 0);
        }

        var assigned = 0;
        var skipped = 0;

        foreach (var userId in targets)
        {
            foreach (var assignment in teamAssignments)
            {
                var existing = await _userAssignments
                    .GetContainerAsync(userId, assignment.RoleId, assignment.HostId, assignment.ContainerId, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    skipped++;
                    continue;
                }

                await _userAssignments
                    .AddAsync(
                        UserRoleAssignment.Assign(
                            Guid.NewGuid(),
                            userId,
                            assignment.RoleId,
                            assignment.HostId,
                            assignment.ContainerId),
                        cancellationToken)
                    .ConfigureAwait(false);

                assigned++;
            }
        }

        _logger.LogInformation(
            "已把团队 {TeamName} 的权限同步到 {Members} 名成员（新增 {Assigned} 条，跳过 {Skipped} 条）",
            team.Name,
            targets.Length,
            assigned,
            skipped);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.TeamSync,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.Team,
                TargetId = team.Id.ToString("D"),
                TargetName = team.Name,
                Detail = $"成员={targets.Length};新增={assigned};跳过={skipped}",
            },
            cancellationToken).ConfigureAwait(false);

        return new TeamSyncResult(assigned, skipped, targets.Length);
    }

    /// <summary>
    /// 确认团队存在。
    /// </summary>
    /// <param name="teamId">团队主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队实体。</returns>
    /// <exception cref="BusinessException">团队不存在（<c>1204</c>）。</exception>
    private async Task<Team> RequireTeamAsync(Guid teamId, CancellationToken cancellationToken)
    {
        return await _teams.QueryByIdAsync(teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.TeamNotFound, "团队不存在");
    }

    /// <summary>
    /// 读取必填的团队主键。
    /// </summary>
    /// <param name="teamId">团队主键（可空）。</param>
    /// <returns>团队主键。</returns>
    /// <exception cref="BusinessException">未提供（<c>1000</c>）。</exception>
    private static Guid RequireTeamId(Guid? teamId)
    {
        if (teamId is not { } id || id == Guid.Empty)
        {
            throw BusinessException.Validation("teamId 不能为空");
        }

        return id;
    }

    /// <summary>
    /// 读取必填的用户主键集合。
    /// </summary>
    /// <param name="userIds">用户主键集合（可空）。</param>
    /// <returns>用户主键集合。</returns>
    /// <exception cref="BusinessException">未提供或为空（<c>1000</c>）。</exception>
    private static IReadOnlyList<Guid> RequireUserIds(IReadOnlyList<Guid>? userIds)
    {
        if (userIds is null || userIds.Count == 0)
        {
            throw BusinessException.Validation("userIds 不能为空");
        }

        return [.. userIds.Where(id => id != Guid.Empty).Distinct()];
    }

    /// <summary>
    /// 解析角色。
    /// </summary>
    /// <param name="request">授权请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色实体。</returns>
    /// <exception cref="BusinessException">参数非法（<c>1000</c>）或角色不存在（<c>1510</c>）。</exception>
    private async Task<Role> ResolveRoleAsync(
        TeamRoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RoleId is { } roleId && roleId != Guid.Empty)
        {
            return await _roles.QueryByIdAsync(roleId, cancellationToken).ConfigureAwait(false)
                ?? throw new BusinessException(ErrorCodes.RoleNotFound, "角色不存在");
        }

        if (String.IsNullOrWhiteSpace(request.Role))
        {
            throw BusinessException.Validation("role 或 roleId 至少提供一个");
        }

        var name = request.Role.Trim();

        return await _roles.FindByNameAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.RoleNotFound, $"角色「{name}」不存在");
    }

    /// <summary>
    /// 解析作用域主机。
    /// </summary>
    /// <param name="hostName">主机名（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机；未指定时返回 <see langword="null"/>（全局作用域）。</returns>
    /// <exception cref="BusinessException">主机不存在（<c>1300</c>）。</exception>
    private async Task<DockerHost?> ResolveHostAsync(string? hostName, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        var name = hostName.Trim();

        return await _hosts.FindByNameAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{name}」不存在");
    }

    /// <summary>
    /// 批量统计各团队的授权数。
    /// </summary>
    /// <param name="teamIds">团队主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>团队主键 → 授权数。</returns>
    private async Task<IReadOnlyDictionary<Guid, int>> CountAssignmentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken)
    {
        var rows = await _assignments.ListByTeamsAsync(teamIds, cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(row => row.TeamId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>
    /// 给团队授权补齐展示用的角色名与主机名。
    /// </summary>
    /// <param name="rows">授权记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补齐后的摘要列表。</returns>
    private async Task<IReadOnlyList<TeamRoleAssignmentSummary>> DecorateAssignmentsAsync(
        IReadOnlyList<TeamRoleAssignment> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var roles = await _roles
            .QueryListByIdsAsync([.. rows.Select(row => row.RoleId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        var roleById = roles.ToDictionary(role => role.Id);

        var hostIds = rows.Where(row => row.HostId is not null).Select(row => row.HostId!.Value).Distinct().ToArray();
        var hosts = hostIds.Length == 0
            ? []
            : await _hosts.QueryListByIdsAsync(hostIds, cancellationToken).ConfigureAwait(false);

        var hostNameById = hosts.ToDictionary(host => host.Id, host => host.Name);

        return [.. rows.Select(row => new TeamRoleAssignmentSummary(
            row.Id,
            row.RoleId,
            roleById.TryGetValue(row.RoleId, out var role) ? role.Name : row.RoleId.ToString("D"),
            roleById.TryGetValue(row.RoleId, out var displayRole) ? displayRole.DisplayName : null,
            row.HostId,
            row.HostId is { } hostId && hostNameById.TryGetValue(hostId, out var name) ? name : null,
            row.ContainerId))];
    }

    /// <summary>
    /// 读取必填字段，缺失或空白时抛参数异常。
    /// </summary>
    /// <param name="value">字段值。</param>
    /// <param name="field">字段名（用于异常信息）。</param>
    /// <returns>去除首尾空白后的字段值。</returns>
    private static string Require(string? value, string field)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        return value.Trim();
    }
}
