using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Auth;
using Whalefleet.Application.Containers;
using Whalefleet.Application.HostGroups;
using Whalefleet.Application.Hosts;
using Whalefleet.Application.Images;
using Whalefleet.Application.Networks;
using Whalefleet.Application.Permissions;
using Whalefleet.Application.Repositories;
using Whalefleet.Application.Users;
using Whalefleet.Application.Volumes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whalefleet.Application.DependencyInjection;

/// <summary>
/// Application 层服务注册扩展。
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Application 层服务。
    /// </summary>
    /// <remarks>
    /// <para>外部资源接口（<c>IDockerClientFactory</c> / <c>IPasswordHasher</c> / <c>ITokenService</c> /
    /// <c>ILoginAttemptTracker</c> 等）的接口定义在本层、实现在 Infrastructure 层，
    /// 因此这里<b>只</b>注册用例服务。</para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（当前未使用，预留给后续应用配置）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 认证用例（0.0.4）。Scoped：依赖 Scoped 的仓储（其取值来自当前请求）。
        services.AddScoped<AuthService>();

        // 主机管理用例（0.0.8）。同样 Scoped：依赖 Scoped 的仓储与主机访问器。
        services.AddScoped<HostService>();

        // 用户管理用例（0.0.8）。
        services.AddScoped<UserService>();

        // 容器查询用例（0.0.9）。Scoped 是因为主机访问器与仓储都是 Scoped，
        // Docker 客户端本身的复用由 Singleton 的客户端池负责，不靠本服务的生命周期。
        services.AddScoped<ContainerService>();

        // 主机查询用例（可见主机列表 + 单台概览）。Scoped：依赖 Scoped 的仓储、
        // 权限服务与当前用户上下文。
        services.AddScoped<HostQueryService>();

        // 镜像用例（0.0.10）。Scoped：依赖 Scoped 的主机访问器。
        services.AddScoped<ImageService>();

        // 镜像构建与推送（0.4.0）。Scoped：依赖 Scoped 的主机访问器与 仓库凭据解析器。
        services.AddScoped<ImageBuildPushService>();

        // 主机分组用例（0.0.13）。Scoped：依赖 Scoped 的仓储。
        services.AddScoped<HostGroupService>();

        // 角色管理用例（0.1.0）。Scoped：依赖 Scoped 的角色 / 分配仓储。
        services.AddScoped<RoleService>();

        // 角色分配用例（0.1.0）。Scoped：依赖 Scoped 的分配 / 角色 / 用户 / 主机仓储。
        services.AddScoped<RoleAssignmentService>();

        // 数据卷用例（0.2.0）。Scoped：与镜像同构，只依赖 Scoped 的主机访问器，
        // Docker 客户端本身的复用由 Singleton 的客户端池负责。
        services.AddScoped<VolumeService>();

        // 卷用量的短时缓存（见 VolumeUsageCache 的说明）：必须 **Singleton**——
        // 它的全部价值就是在多个请求之间复用一次几秒钟的 /system/df。
        services.AddSingleton<VolumeUsageCache>();

        // 网络用例（0.2.0）。Scoped：同上。
        services.AddScoped<NetworkService>();

        // 仓库配置用例（0.4.0）。Scoped：依赖 Scoped 的仓储与口令保护器。
        // 同时以 IRepositoryCredentialResolver 的身份暴露**同一实例**——
        // 镜像推送去拿凭据时必须走同一套解密逻辑，不能另起一份实现。
        services.AddScoped<RepositoryService>();
        services.AddScoped<IRepositoryCredentialResolver>(static provider =>
            provider.GetRequiredService<RepositoryService>());

        return services;
    }
}