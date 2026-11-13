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
    /// <summary>
    /// 查看用户列表与详情（<c>user.read</c>）。
    /// </summary>
    public const string UsersRead = "perm.user.read";

    /// <summary>
    /// 创建 / 删除用户、改密码（<c>user.write</c>）。
    /// </summary>
    public const string UsersWrite = "perm.user.write";

    /// <summary>
    /// 查看角色与权限矩阵（<c>role.read</c>）。
    /// </summary>
    public const string RolesRead = "perm.role.read";

    /// <summary>
    /// 增删改角色与角色分配（<c>role.write</c>）。
    /// </summary>
    public const string RolesWrite = "perm.role.write";

    /// <summary>
    /// 新增 / 删除 / 分组主机（<c>host.manage</c>）。
    /// </summary>
    public const string HostsManage = "perm.host.manage";

    /// <summary>
    /// 查看 仓库配置（<c>repository.read</c>；<b>不含口令</b>）。
    /// </summary>
    public const string RepositoriesRead = "perm.repository.read";

    /// <summary>
    /// 增删改 仓库配置与凭据（<c>repository.write</c>）。
    /// </summary>
    public const string RepositoriesWrite = "perm.repository.write";

    /// <summary>
    /// 查看操作审计（<c>audit.read</c>；审计能力排在 1.0.0 之后）。
    /// </summary>
    public const string AuditRead = "perm.audit.read";

    /// <summary>
    /// 查看告警规则与通知渠道（<c>alert.read</c>）。
    /// </summary>
    public const string AlertsRead = "perm.alert.read";

    /// <summary>
    /// 增删改告警规则与通知渠道（<c>alert.write</c>）。
    /// </summary>
    public const string AlertsWrite = "perm.alert.write";

    /// <summary>
    /// 查看团队花名册、成员与团队角色授权（<c>team.read</c>）。
    /// </summary>
    public const string TeamsRead = "perm.team.read";

    /// <summary>
    /// 增删改团队、调整成员与团队角色授权、同步到成员（<c>team.write</c>）。
    /// </summary>
    /// <remarks>
    /// <b>它比 <see cref="UsersWrite"/> 弱</b>：团队只<b>收窄</b>权限、不扩权，
    /// 而团队成员的个人授权仍由 <c>user.write</c> 管理。
    /// </remarks>
    public const string TeamsWrite = "perm.team.write";

    /// <summary>
    /// 查看环境变量（<c>variable.read</c>）。
    /// </summary>
    public const string VariablesRead = "perm.variable.read";

    /// <summary>
    /// 增删改环境变量（<c>variable.write</c>）。
    /// </summary>
    public const string VariablesWrite = "perm.variable.write";
}
