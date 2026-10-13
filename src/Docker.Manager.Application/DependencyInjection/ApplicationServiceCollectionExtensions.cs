using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Auth;
using Docker.Manager.Application.Containers;
using Docker.Manager.Application.Hosts;
using Docker.Manager.Application.Images;
using Docker.Manager.Application.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Docker.Manager.Application.DependencyInjection;

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

        return services;
    }
}