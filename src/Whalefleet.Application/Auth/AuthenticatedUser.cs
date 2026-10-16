namespace Whalefleet.Application.Auth;

/// <summary>
/// 已认证用户的身份摘要（不含任何敏感字段）。
/// </summary>
/// <param name="Id">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="Roles">角色名数组（多角色；新建用户尚未被分配时是<b>空数组</b>）。</param>
/// <param name="Permissions">
/// <b>全局</b>有效权限集（<c>domain.level</c> 字符串数组，按目录顺序）。
/// 主机级的权限不在这里——它随主机而变，由主机列表接口按需带回。
/// </param>
/// <remarks>
/// <para>前端用它渲染顶栏用户信息并决定菜单与操作按钮的可见性。
/// <b>不得</b>包含密码哈希、令牌或凭据。</para>
/// <para><b>⚠️ 它是展示依据，不是安全边界</b>：接口层一律按 <c>IPermissionChecker</c> 查库判定，
/// 前端拿这份数据只是为了让界面不渲染无权使用的按钮。
/// 即便它被篡改，也只能得到一个点了会 403 的界面。</para>
/// </remarks>
public sealed record AuthenticatedUser(
    Guid Id,
    string Username,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
