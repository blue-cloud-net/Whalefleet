namespace Whalefleet.Application.Permissions;

/// <summary>
/// 一条角色分配（管理端点的返回契约）。
/// </summary>
/// <param name="Id">分配记录主键。</param>
/// <param name="UserId">被分配的用户主键。</param>
/// <param name="Username">用户名（冗余自用户表：管理界面要显示人名，不该自己再查一次）。</param>
/// <param name="RoleId">角色主键。</param>
/// <param name="Role">角色名。</param>
/// <param name="RoleDisplayName">角色可读名称（可为空）。</param>
/// <param name="HostId">
/// 作用域主机主键；<see langword="null"/> 表示<b>全局分配</b>。
/// </param>
/// <param name="HostName">
/// 作用域主机名；<see langword="null"/> 表示全局分配。主机名是对外标识（路径里用的就是它），
/// 而 <see cref="HostId"/> 是仅供审计与外键的 GUID。
/// </param>
/// <param name="LastModificationTime">最后一次变更时间（UTC）；从未变更过为 <see langword="null"/>。</param>
/// <remarks>
/// <para><b>为什么同时给 GUID 与主机名</b>：管理界面在主机视图里要按名字显示，
/// 而回填与审计要用 GUID。让客户端自己拿 GUID 去查主机，等于把一次额外的往返写进每次渲染。</para>
/// <para><b>为什么「全局分配」要显式表达</b>：它与「分配在某台主机上」是两个不同的事实，
/// 而同一个角色在两种事实下的行为不同（全局权限是否生效）。用一个可空的
/// <see cref="HostId"/> 表达，界面才能把这两行区分开来渲染。</para>
/// </remarks>
public sealed record RoleAssignmentSummary(
    Guid Id,
    Guid UserId,
    string Username,
    Guid RoleId,
    string Role,
    string? RoleDisplayName,
    Guid? HostId,
    string? HostName,
    DateTime? LastModificationTime);

/// <summary>
/// 一个角色的完整投影（角色列表 / 详情端点）。
/// </summary>
/// <param name="Id">角色主键。</param>
/// <param name="Name">角色标识。</param>
/// <param name="DisplayName">可读名称；可为空。</param>
/// <param name="Description">职责描述；可为空。</param>
/// <param name="IsBuiltIn">是否内置角色（不可删除，权限不可修改）。</param>
/// <param name="IsDefault">是否默认角色（创建用户未指定角色时采用）。</param>
/// <param name="Permissions">权限集（规范化后按目录顺序）。</param>
/// <param name="AssignmentCount">当前有效分配数（界面据此提示「仍在使用」）。</param>
public sealed record RoleSummary(
    Guid Id,
    string Name,
    string? DisplayName,
    string? Description,
    bool IsBuiltIn,
    bool IsDefault,
    IReadOnlyList<string> Permissions,
    int AssignmentCount);

/// <summary>
/// 权限目录的一条（前端权限矩阵的列定义）。
/// </summary>
/// <param name="Name">权限标识（<c>domain.level</c>）。</param>
/// <param name="Domain">资源域（<c>host</c> / <c>container</c> / <c>user</c> …），矩阵的分组依据。</param>
/// <param name="Level">档位（<c>read</c> / <c>write</c> / <c>admin</c> / <c>manage</c>）。</param>
/// <param name="Scope">作用域名（<c>global</c> / <c>host</c> / <c>container</c>）。</param>
public sealed record PermissionDefinition(
    string Name,
    string Domain,
    string Level,
    string Scope);
