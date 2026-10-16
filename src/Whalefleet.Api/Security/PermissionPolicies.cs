namespace Whalefleet.Api.Security;

/// <summary>
/// 权限策略名的单一来源。
/// </summary>
/// <remarks>
/// <para><b>为什么用常量而不是字符串字面量</b>：策略名分散在控制器与 DI 注册两处，
/// 任何一侧改名都会在<b>运行时</b>才炸成「403 而不是 404」这种极难定位的症状
/// （端点存在，只是没人能通过）。常量让改名先撞编译错误。</para>
/// <para><b>命名口径</b>：<c>perm.&lt;权限标识&gt;</c>。权限标识本身是
/// <c>domain.level</c>，于是策略名形如 <c>perm.user.write</c>——
/// 看名字就知道它要什么，不需要再回查它对应哪个端点。</para>
/// </remarks>
public static class PermissionPolicies
{
    /// <summary>查看用户列表与详情（<c>user.read</c>）。</summary>
    public const string UsersRead = "perm.user.read";

    /// <summary>创建 / 删除用户、改密码（<c>user.write</c>）。</summary>
    public const string UsersWrite = "perm.user.write";

    /// <summary>查看角色与权限矩阵（<c>role.read</c>）。</summary>
    public const string RolesRead = "perm.role.read";

    /// <summary>增删改角色与角色分配（<c>role.write</c>）。</summary>
    public const string RolesWrite = "perm.role.write";

    /// <summary>新增 / 删除 / 分组主机（<c>host.manage</c>）。</summary>
    public const string HostsManage = "perm.host.manage";

    /// <summary>查看操作审计（<c>audit.read</c>；审计能力排在 1.0.0 之后）。</summary>
    public const string AuditRead = "perm.audit.read";
}
