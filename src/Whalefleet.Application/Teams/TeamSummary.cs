namespace Whalefleet.Application.Teams;

/// <summary>
/// 团队列表项（管理端点的返回契约）。
/// </summary>
/// <param name="Id">团队主键。</param>
/// <param name="Name">团队标识。</param>
/// <param name="DisplayName">可读名称；可为空。</param>
/// <param name="Description">备注；可为空。</param>
/// <param name="MemberCount">当前成员数。</param>
/// <param name="RoleAssignmentCount">当前团队角色授权数（权限上限的条数）。</param>
/// <param name="CreationTime">创建时间（UTC）。</param>
public sealed record TeamSummary(
    Guid Id,
    string Name,
    string? DisplayName,
    string? Description,
    int MemberCount,
    int RoleAssignmentCount,
    DateTimeOffset CreationTime);

/// <summary>
/// 团队详情（含成员与角色授权）。
/// </summary>
/// <param name="Summary">团队基本信息。</param>
/// <param name="Members">成员列表。</param>
/// <param name="RoleAssignments">团队角色授权列表。</param>
public sealed record TeamDetail(
    TeamSummary Summary,
    IReadOnlyList<TeamMemberSummary> Members,
    IReadOnlyList<TeamRoleAssignmentSummary> RoleAssignments);

/// <summary>
/// 团队成员摘要。
/// </summary>
/// <param name="UserId">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="JoinedAt">加入时间（UTC）。</param>
/// <param name="IsDisabled">账号是否已停用。</param>
/// <param name="MustChangePassword">账号是否必须先改密。</param>
public sealed record TeamMemberSummary(
    Guid UserId,
    string Username,
    DateTimeOffset JoinedAt,
    bool IsDisabled,
    bool MustChangePassword);

/// <summary>
/// 团队角色授权摘要（<b>权限上限</b>，不是授权来源）。
/// </summary>
/// <param name="Id">授权记录主键。</param>
/// <param name="RoleId">角色主键。</param>
/// <param name="Role">角色名。</param>
/// <param name="RoleDisplayName">角色可读名称；可为空。</param>
/// <param name="HostId">作用域主机主键；<see langword="null"/> 表示全局。</param>
/// <param name="HostName">作用域主机名；<see langword="null"/> 表示全局。</param>
/// <param name="ContainerId">容器完整 ID；<see langword="null"/> 表示非容器级。</param>
public sealed record TeamRoleAssignmentSummary(
    Guid Id,
    Guid RoleId,
    string Role,
    string? RoleDisplayName,
    Guid? HostId,
    string? HostName,
    string? ContainerId);

/// <summary>
/// 分页结果。
/// </summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="PageIndex">当前页码（从 0 开始）。</param>
/// <param name="PageSize">每页条数（夹紧后的实际值）。</param>
public sealed record TeamListResult(
    IReadOnlyList<TeamSummary> Items,
    int Total,
    int PageIndex,
    int PageSize);

/// <summary>
/// 同步结果：把团队权限写入成员个人授权的执行结果。
/// </summary>
/// <param name="Assigned">实际写入（新建）的分配条数。</param>
/// <param name="Skipped">已存在而跳过的分配条数。</param>
/// <param name="Members">被同步的成员数。</param>
public sealed record TeamSyncResult(int Assigned, int Skipped, int Members);
