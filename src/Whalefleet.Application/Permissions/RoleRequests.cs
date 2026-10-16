namespace Whalefleet.Application.Permissions;

/// <summary>
/// 角色端点的请求契约。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态。校验统一在
/// <see cref="RoleService"/> 里做并给出可读消息（详见 <c>Users/UserRequests.cs</c> 的同类说明）。
/// </remarks>
public sealed record ListRolesRequest
{
    /// <summary>
    /// 是否只返回<b>自定义</b>角色（排除内置角色）。
    /// </summary>
    /// <remarks>
    /// 分配场景几乎总是要全部角色（含内置），而权限矩阵只想编辑自定义的那些——
    /// 一个开关比让前端各自过滤更不容易漏。
    /// </remarks>
    public bool? CustomOnly { get; init; }
}

/// <summary>
/// 创建角色的请求契约。
/// </summary>
public sealed record CreateRoleRequest
{
    /// <summary>角色标识；必填（自动转小写）。</summary>
    public string? Name { get; init; }

    /// <summary>可读名称；可空，为空时界面回退显示 <see cref="Name"/>。</summary>
    public string? DisplayName { get; init; }

    /// <summary>职责描述；可空。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 初始权限集；可为空（等价于「一个什么都不干的角色」）。
    /// </summary>
    /// <remarks>
    /// 允许为空是有意的：先建角色、后配权限是真实的管理路径，
    /// 而此时「不选任何权限」不该被当成非法输入。
    /// </remarks>
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>
/// 修改角色的请求契约（定位 + 新内容）。
/// </summary>
/// <remarks>
/// <b>定位与内容合并成一个对象</b>：ASP.NET Core 每个动作<b>只允许一个</b>请求体参数，
/// 写成 <c>RoleSelectorRequest</c> + <c>UpdateRoleRequest</c> 两个 <c>[FromBody]</c>
/// 会在应用启动时抛 <c>InvalidOperationException</c>——而且是在第一次构造路由表时，
/// 也就是**整个进程起不来**，而不是某个端点 400。
/// </remarks>
public sealed record UpdateRoleRequest : RoleSelectorRequest
{
    /// <summary>可读名称；可空。</summary>
    public string? DisplayName { get; init; }

    /// <summary>职责描述；可空。</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 覆盖式设置角色权限集的请求契约（定位 + 新权限集）。
/// </summary>
/// <remarks>同样是因为「一个动作一个请求体」而与定位条件合并，理由见 <see cref="UpdateRoleRequest"/>。</remarks>
public sealed record SetRolePermissionsRequest : RoleSelectorRequest
{
    /// <summary>
    /// 目标权限集；<b>必填</b>，但允许是空数组（「收回这个角色的全部权限」是合法操作）。
    /// </summary>
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>
/// 按标识（或主键）定位角色的请求契约。
/// </summary>
/// <remarks>
/// 用<b>角色名</b>而不是 <c>roleId</c>：名字是稳定且可读的对外标识，
/// 路径里的主机名也是这么处理的——前端不必为了调接口先查一遍 GUID。
/// <c>roleId</c> 作为体字段保留，是给「从列表里选中一行」的场景用的。
/// </remarks>
/// <remarks>
/// <b>刻意不密封</b>：<see cref="UpdateRoleRequest"/> 与 <see cref="SetRolePermissionsRequest"/>
/// 需要继承它来表达「定位条件 + 请求内容」——因为 ASP.NET Core 每个动作只允许一个请求体参数。
/// </remarks>
public record RoleSelectorRequest
{
    /// <summary>角色主键；<see cref="Role"/> 与 <see cref="RoleId"/> 至少给一个。</summary>
    public Guid? RoleId { get; init; }

    /// <summary>角色标识（不区分大小写）；<see cref="RoleId"/> 与 <see cref="Role"/> 至少给一个。</summary>
    public string? Role { get; init; }
}
