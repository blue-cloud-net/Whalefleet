namespace Whalefleet.Application.Permissions;

/// <summary>
/// 主机视角的分配查询请求（<c>admin/hosts/{hostId}/roles/list</c>）。
/// </summary>
public sealed record ListHostRoleAssignmentsRequest
{
    /// <summary>
    /// 目标用户主键；<b>省略时列出该主机上的全部分配</b>。
    /// </summary>
    /// <remarks>
    /// 主机管理页要回答的是「谁能访问这台机器」，因此默认列全部；
    /// 用户管理页嵌入对话框时已经知道自己在查谁，带上 <c>userId</c> 可以少传一些数据。
    /// </remarks>
    public Guid? UserId { get; init; }
}

/// <summary>
/// 分配角色的请求契约（全局或主机作用域）。
/// </summary>
/// <remarks>
/// <para><b>为什么用角色名而不是直接给权限</b>：分配的是<b>角色</b>。
/// 界面若让管理员直接勾权限，配出来的授权就绕过了角色——那等于把「谁能改别人的权限」
/// 和「谁能操作机器」合成一件事，出问题时无法回答「他为什么有这个权限」。</para>
/// <para><b><c>host</c> 省略即全局分配</b>。这与主机中心的端点不同：
/// 那个端点里主机在路径上，本端点是「在用户视角下选择作用域」。</para>
/// </remarks>
public sealed record AssignRoleRequest
{
    /// <summary>目标用户主键；必填。</summary>
    public Guid? UserId { get; init; }

    /// <summary>角色主键；<see cref="RoleId"/> 与 <see cref="Role"/> 至少提供一个。</summary>
    public Guid? RoleId { get; init; }

    /// <summary>角色标识（不区分大小写）；<see cref="RoleId"/> 与 <see cref="Role"/> 至少提供一个。</summary>
    public string? Role { get; init; }

    /// <summary>作用域主机名；<b>省略或为空表示全局分配</b>。</summary>
    public string? Host { get; init; }
}

/// <summary>
/// 撤销角色分配的请求契约。
/// </summary>
/// <remarks>
/// 与授予不同，撤销<b>没有「覆盖」语义</b>：一次撤销就是撤掉那一条分配。
/// 「把这个人的权限改成什么样」属于分配列表的差异计算，由界面提交若干次分配/撤销表达，
/// 服务层不替它猜——猜错的方向永远是「权限更多」。
/// </remarks>
public sealed record RevokeRoleAssignmentRequest
{
    /// <summary>目标用户主键；必填。</summary>
    public Guid? UserId { get; init; }

    /// <summary>角色主键；<see cref="RoleId"/> 与 <see cref="Role"/> 至少提供一个。</summary>
    public Guid? RoleId { get; init; }

    /// <summary>角色标识（不区分大小写）；<see cref="RoleId"/> 与 <see cref="Role"/> 至少提供一个。</summary>
    public string? Role { get; init; }

    /// <summary>作用域主机名；<b>省略或为空表示撤销全局分配</b>。</summary>
    public string? Host { get; init; }
}
