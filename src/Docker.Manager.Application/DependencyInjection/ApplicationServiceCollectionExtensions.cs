using Docker.Manager.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Docker.Manager.Application.DependencyInjection;

/// <summary>
/// Application 层服务注册扩展。
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Application 层服务。当前 0.0.1 阶段暂无可注册的应用服务，
    /// 仅保留 <see cref="IDockerClientFactory"/> 的接口位置约定（实现在 Infrastructure 层）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（当前未使用，预留给后续应用配置）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 0.0.1 无应用服务可注册。接口在 Abstractions/ 下，实现在 Infrastructure 层。
        // 后续迭代（0.0.4 起）将注册 AuthService / ContainerService / ImageService 等。
        return services;
    }
}