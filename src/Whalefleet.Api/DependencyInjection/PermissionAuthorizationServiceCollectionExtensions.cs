using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Whalefleet.Api.Security;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Api.DependencyInjection;

/// <summary>
/// 权限授权的注册扩展。
/// </summary>
/// <remarks>
/// <para><b>为什么不用内置的 <c>AddAuthorization(options =&gt; options.AddPolicy(…))</c></b>：
/// 那条路只能接 <c>RequireRole</c> / <c>RequireClaim</c> 这类<b>基于声明</b>的要求，
/// 而授权事实已改为查库（见 <see cref="PermissionRequirement"/>）。
/// 自定义 <see cref="PermissionAuthorizationHandler"/> 才能表达「这个人当前是否具备
/// 某项<b>全局</b>权限」。</para>
/// <para><b>副作用收益</b>：声明是登录时的快照，因此角色与权限的改动<b>立即生效</b>，
/// 不再需要等 access token 过期才生效。</para>
/// </remarks>
public static class PermissionAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// 注册权限授权的处理器与全部内置策略。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <see langword="null"/>。</exception>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 处理器要查库，因此不能是 Singleton；框架每次授权都从容器解析它，
        // Scoped 顺带让 IPermissionChecker 的请求级缓存生效。
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            AddPolicy(options, PermissionPolicies.UsersRead, Permission.UserRead);
            AddPolicy(options, PermissionPolicies.UsersWrite, Permission.UserWrite);
            AddPolicy(options, PermissionPolicies.RolesRead, Permission.RoleRead);
            AddPolicy(options, PermissionPolicies.RolesWrite, Permission.RoleWrite);
            AddPolicy(options, PermissionPolicies.HostsManage, Permission.HostManage);
            AddPolicy(options, PermissionPolicies.AuditRead, Permission.AuditRead);
        });

        return services;
    }

    /// <summary>
    /// 注册一条权限策略：挂一个 <see cref="PermissionRequirement"/> 要求，
    /// 由已注册的 <see cref="PermissionAuthorizationHandler"/> 查库判定。
    /// </summary>
    /// <param name="options">授权选项。</param>
    /// <param name="policyName">策略名（见 <see cref="PermissionPolicies"/>）。</param>
    /// <param name="permission">该策略要求的全局权限标识。</param>
    private static void AddPolicy(
        AuthorizationOptions options,
        string policyName,
        string permission) =>
        options.AddPolicy(
            policyName,
            policy => policy.AddRequirements(new PermissionRequirement(permission)));
}
