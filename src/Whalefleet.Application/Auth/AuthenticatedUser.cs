namespace Whalefleet.Application.Auth;

/// <summary>
/// 已认证用户的身份摘要（不含任何敏感字段）。
/// </summary>
/// <param name="Id">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="Role">角色名（如 <c>admin</c>）。</param>
/// <remarks>
/// 前端用它渲染顶栏用户信息并决定菜单可见性（<c>requiresAdmin</c>）。
/// <b>不得</b>包含密码哈希、令牌或凭据。
/// </remarks>
public sealed record AuthenticatedUser(Guid Id, string Username, string Role);
