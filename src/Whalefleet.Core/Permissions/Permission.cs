namespace Whalefleet.Core.Permissions;

/// <summary>
/// 权限的作用域：一条权限在什么范围内才有意义。
/// </summary>
/// <remarks>
/// <para><b>为什么权限要分作用域</b>：「能不能管用户」与「能不能在这台主机上启停容器」是两类不同的授权事实。
/// 少了作用域这一维，"把 admin 角色分配给某台主机"就分不清是「仅在这台机器上是管理员」
/// 还是「成了系统管理员」——后者是一次静默提权。</para>
/// <para><b><see cref="Container"/> 为 0.5.0 预留</b>：目录里已经存在容器级权限，
/// 但 0.1.0 <b>只开放</b> <see cref="Global"/> 与 <see cref="Host"/> 两条分配路径。
/// 预先留出这一档，是为了让 0.5.0 只需要"打通一条已存在的路径"，而不必回头改已有角色的权限定义。</para>
/// </remarks>
public enum PermissionScope
{
    /// <summary>
    /// 全局：与具体主机无关（管理用户、管理角色、新增主机、查看审计）。
    /// </summary>
    Global,

    /// <summary>
    /// 主机级：在一台主机内部有效（读写该主机的容器 / 镜像 / 网络 / 卷、改主机自身配置）。
    /// </summary>
    Host,

    /// <summary>
    /// 容器级：针对单台主机上的某个容器（0.5.0 落地）。
    /// </summary>
    Container,
}

/// <summary>
/// 权限目录：一组**静态封闭**的功能能力标识，命名为 <c>domain.level</c>。
/// </summary>
/// <remarks>
/// <para><b>刻意不建表</b>：「本版本有没有这个功能」是**编译期**事实。
/// 放进表里只会让运行时可配的角色逐渐偏离代码真正能强制的范围——
/// 删掉一行 <c>container.write</c>，代码里的鉴权分支并不会因此消失。</para>
/// <para><b>与角色的分工</b>：权限是「能做什么」，角色是「一组权限 + 一个可复用的名字」，
/// 而「在哪台主机上生效」由 <see cref="UserRoleAssignment"/> 承担。
/// 三者分立，因此「新建一个角色」不会隐式带来任何授权。</para>
/// <para><b>命名</b>：全小写、<c>domain.level</c> 两段。字符集限定为 ASCII 字母、数字与
/// <c>. _ -</c>——它会以文本形态进 JSON、进 <c>role_permissions</c> 表、并由前端原样回传。</para>
/// <para><b>粒度</b>：粗粒度的「资源域 × 档位」。细到每个原子操作会让权限常量、内置角色种子、
/// 权限矩阵 UI 与测试一起膨胀，而 Docker 侧的操作集还在变。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Permission 是本项目的领域名词（权限目录），非 CA1711 所针对的系统命名空间后缀。")]
public static class Permission
{
    // ==================== Host 作用域 ====================

    /// <summary>查看主机本身（可见性判定的依据）。</summary>
    public const string HostRead = "host.read";

    /// <summary>改动主机自身配置（连接方式、端点、凭据、启停）。</summary>
    public const string HostAdmin = "host.admin";

    /// <summary>查看主机上的容器。</summary>
    public const string ContainerRead = "container.read";

    /// <summary>启停 / 重建 / 删除容器。</summary>
    public const string ContainerWrite = "container.write";

    /// <summary>查看主机上的镜像。</summary>
    public const string ImageRead = "image.read";

    /// <summary>拉取 / 删除镜像。</summary>
    public const string ImageWrite = "image.write";

    /// <summary>查看主机上的网络。</summary>
    public const string NetworkRead = "network.read";

    /// <summary>创建 / 删除网络。</summary>
    public const string NetworkWrite = "network.write";

    /// <summary>查看主机上的卷。</summary>
    public const string VolumeRead = "volume.read";

    /// <summary>创建 / 删除卷。</summary>
    public const string VolumeWrite = "volume.write";

    // ==================== Global 作用域 ====================

    /// <summary>查看用户列表与详情。</summary>
    public const string UserRead = "user.read";

    /// <summary>创建 / 删除用户、变更其角色分配与密码。</summary>
    public const string UserWrite = "user.write";

    /// <summary>查看角色列表与权限矩阵。</summary>
    public const string RoleRead = "role.read";

    /// <summary>增删改自定义角色、调整其权限。</summary>
    public const string RoleWrite = "role.write";

    /// <summary>新增 / 删除 / 分组主机。</summary>
    /// <remarks>
    /// 与 <see cref="HostAdmin"/> 分开：前者是「这台机器归我管」（可在主机范围内授予），
    /// 后者是「这台机器归全局管」（不能通过任何主机分配授予）。
    /// </remarks>
    public const string HostManage = "host.manage";

    /// <summary>查看私有 Registry 配置（地址、别名、账号；<b>不含口令</b>）。</summary>
    public const string RegistryRead = "registry.read";

    /// <summary>增删改私有 Registry 配置与凭据。</summary>
    public const string RegistryWrite = "registry.write";

    /// <summary>查看操作审计（审计能力本身排在 1.0.0 之后，此处只占位）。</summary>
    public const string AuditRead = "audit.read";

    /// <summary>全局作用域的全部权限标识。</summary>
    public static readonly IReadOnlyList<string> Globals =
    [
        UserRead,
        UserWrite,
        RoleRead,
        RoleWrite,
        HostManage,
        RegistryRead,
        RegistryWrite,
        AuditRead,
    ];

    /// <summary>主机作用域的全部权限标识。</summary>
    public static readonly IReadOnlyList<string> Hosts =
    [
        HostRead,
        HostAdmin,
        ContainerRead,
        ContainerWrite,
        ImageRead,
        ImageWrite,
        NetworkRead,
        NetworkWrite,
        VolumeRead,
        VolumeWrite,
    ];

    /// <summary>目录中的全部权限标识（Host 在前、Global 在后，与界面分组顺序一致）。</summary>
    public static readonly IReadOnlyList<string> All = [.. Hosts, .. Globals];

    /// <summary>
    /// 目录中的权限 → 作用域。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Dictionary{TKey, TValue}"/> 而不是「解析字符串前缀」：
    /// 前缀解析会让 <c>host.read</c> 与 <c>host.manage</c> 落到不同的判定分支上，
    /// 而两者在授权语义上毫无关系——一个是主机域权限，一个是全局权限。
    /// </remarks>
    private static readonly Dictionary<string, PermissionScope> Scopes = BuildScopes();

    /// <summary>
    /// 权限标识是否存在于目录中。
    /// </summary>
    /// <param name="permission">权限标识（大小写不敏感，可含首尾空白）。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    public static bool Exists(string? permission) =>
        !String.IsNullOrWhiteSpace(permission) &&
        Scopes.ContainsKey(Normalize(permission));

    /// <summary>
    /// 取权限的作用域。
    /// </summary>
    /// <param name="permission">权限标识（大小写不敏感，可含首尾空白）。</param>
    /// <returns>该权限的作用域。</returns>
    /// <exception cref="ArgumentException">权限标识不在目录中。</exception>
    public static PermissionScope ScopeOf(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        if (Scopes.TryGetValue(Normalize(permission), out var scope))
        {
            return scope;
        }

        throw new ArgumentException($"未知的权限标识：{permission}", nameof(permission));
    }

    /// <summary>
    /// 批量取作用域。
    /// </summary>
    /// <param name="permissions">权限标识集合。</param>
    /// <returns>权限 → 作用域的映射；输入非法时抛异常（不返回部分结果）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="permissions"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">任一权限标识不在目录中。</exception>
    public static IReadOnlyDictionary<string, PermissionScope> MapScopes(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var map = new Dictionary<string, PermissionScope>(StringComparer.Ordinal);

        foreach (var permission in permissions)
        {
            if (String.IsNullOrWhiteSpace(permission))
            {
                throw new ArgumentException("权限标识不得为空", nameof(permissions));
            }

            map[Normalize(permission)] = ScopeOf(permission);
        }

        return map;
    }

    private static Dictionary<string, PermissionScope> BuildScopes()
    {
        var map = new Dictionary<string, PermissionScope>(StringComparer.Ordinal);

        foreach (var permission in Hosts)
        {
            map[permission] = PermissionScope.Host;
        }

        foreach (var permission in Globals)
        {
            map[permission] = PermissionScope.Global;
        }

        return map;
    }

    private static string Normalize(string permission) => permission.Trim().ToLowerInvariant();
}
