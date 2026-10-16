using Microsoft.AspNetCore.Authorization;

namespace Whalefleet.Api.Security;

/// <summary>
/// 按<b>权限</b>（而不是角色）授权的要求。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 <c>[Authorize(Roles = "admin")]</c></b>：它读令牌里的 <c>role</c> claim，
/// 而带作用域的角色分配根本装不进单个 claim——同一人可能在 A 主机是运维、在 B 主机是只读。
/// 更实际的问题是<b>生效时机</b>：claim 是登录时的快照，
/// 改了角色或权限要等 access token 过期才对新请求生效。</para>
/// <para><b>现在改成了</b>：每次请求查库判定（<c>PermissionChecker</c>，请求内缓存），
/// 因此权限与角色的改动<b>立即生效</b>。</para>
/// <para><b>本要求只表达「全局权限」</b>：主机作用域端点的权限判定有两处，
/// 且都不能靠授权策略——</para>
/// <list type="bullet">
///   <item><description><c>host.read</c>（能否看到这台机器）：由 <c>CurrentHostMiddleware</c>
///   在解析主机之后判定，它知道 <c>hostId</c>；</description></item>
///   <item><description>其余主机域权限（<c>container.write</c> 等）：由动作内调用
///   <c>IPermissionChecker</c> 判定，那里同样已经知道 <c>hostId</c>。</description></item>
/// </list>
/// <para>授权发生在 <c>UseAuthorization</c>，而当前主机在它<b>之后</b>才被解析，
/// 因此策略层拿不到 <c>hostId</c>——把它做成「看起来能按主机判、实际退化」的参数
/// 只会让后来者误以为主机级权限已经被策略管住了。</para>
/// </remarks>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// 初始化要求。
    /// </summary>
    /// <param name="permission">需要的<b>全局</b>权限标识（<c>domain.level</c>）。</param>
    /// <exception cref="ArgumentException"><paramref name="permission"/> 为空。</exception>
    public PermissionRequirement(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        Permission = permission.Trim().ToLowerInvariant();
    }

    /// <summary>需要的全局权限标识。</summary>
    public string Permission { get; }
}
