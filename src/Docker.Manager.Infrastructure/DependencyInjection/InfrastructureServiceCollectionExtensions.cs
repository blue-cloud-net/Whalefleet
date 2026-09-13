using Docker.DotNet;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Infrastructure.Docker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Docker.Manager.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure 层服务注册扩展。
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Infrastructure 层服务：
    /// - <see cref="DockerClientFactoryOptions"/>（绑定 <c>Docker:DefaultHost</c> 配置节）
    /// - <see cref="IDockerClientFactory"/> → <see cref="DockerClientFactory"/>（Singleton）
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例。</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<DockerClientFactoryOptions>()
            .Bind(configuration.GetSection(DockerClientFactoryOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IDockerClientFactory, DockerClientFactory>();

        return services;
    }
}