using Microsoft.AspNetCore.Authorization;
using Whalefleet.Application.Abstractions;

namespace Whalefleet.Api.Security;

/// <summary>
/// 权限授权处理器：查库判定，<b>不</b>读令牌里的角色 claim。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 <c>RolesAuthorizationRequirement</c></b>：那个处理器只比字符串，
/// 而授权事实已改为查库（见 <see cref="PermissionRequirement"/> 的说明）。</para>
/// <para><b>未认证一律不 <c>Succeed</c></b>：让框架统一映射成 401 而不是 403。
/// 「你没登录」与「你权限不够」在语义上不是一回事，混成同一个码
/// 会让前端无法区分该不该跳登录页。</para>
/// </remarks>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionChecker _permissions;
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// 初始化授权处理器。
    /// </summary>
    /// <param name="permissions">权限判定。</param>
    /// <param name="currentUser">当前登录用户。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public PermissionAuthorizationHandler(IPermissionChecker permissions, ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);

        _permissions = permissions;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (!_currentUser.IsAuthenticated || _currentUser.UserId is not { } userId)
        {
            return;
        }

        // hostId 传 null = 按全局权限判定。本处理器只服务全局权限；
        // 主机域权限的判定点在 CurrentHostMiddleware 与各动作内（那里才知道 hostId）。
        if (await _permissions
            .HasPermissionAsync(userId, null, requirement.Permission)
            .ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
