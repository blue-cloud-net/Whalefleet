using Docker.Manager.Application.Abstractions;
using Docker.Manager.Infrastructure.Docker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docker.Manager.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure 层服务注册扩展。
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Infrastructure 层服务：
    /// - <see cref="DockerClientFactoryOptions"/>（绑定 <c>Docker</c> 配置节，含启动时校验）
    /// - <see cref="IDockerClientFactory"/> / <see cref="IDockerClientCacheMaintenance"/>
    ///   → <see cref="DockerClientFactory"/>（Singleton）
    /// - <see cref="DockerClientCacheReaper"/>（后台空闲回收）
    /// - <see cref="TimeProvider"/>（若调用方尚未注册）
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须是单例</b>：客户端缓存要跨请求存活，否则「切换主机不重建连接」
    /// 就退化成了每次请求重连。</para>
    /// <para><b>配置校验</b>：主机列表为空、主机名重复/为空、端点无法规范化，
    /// 都会在<b>启动时</b>失败（<c>ValidateOnStart</c>），而不是拖到第一次连接。</para>
    /// </remarks>
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
            .Validate(
                options => options.Hosts is { Count: > 0 },
                "Docker:Hosts 至少需要配置一个主机")
            .Validate(
                options => options.Hosts.All(host => !String.IsNullOrWhiteSpace(host.Name)),
                "Docker:Hosts 的主机名（Name）不能为空")
            .Validate(
                options => options.Hosts
                    .Select(host => host.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == options.Hosts.Count,
                "Docker:Hosts 的主机名（Name）不能重复（不区分大小写）")
            .Validate(
                options => options.Hosts.All(host =>
                    DockerEndpointNormalizer.TryNormalize(host.Type, host.Endpoint, out _, out _)),
                "Docker:Hosts 的端点非法：socket 需为绝对路径（如 /var/run/docker.sock），"
                + "TCP 形如 tcp://host:2375")
            .Validate(
                options => options.ClientCache.IdleTimeout >= TimeSpan.Zero
                    && options.ClientCache.ReapInterval >= TimeSpan.Zero,
                "Docker:ClientCache 的 IdleTimeout / ReapInterval 不能为负值")
            .ValidateOnStart();

        // 工厂本体只注册一次，再以两个身份暴露同一实例：
        // 业务侧用 IDockerClientFactory，后台回收任务用 IDockerClientCacheMaintenance。
        // 不能写成 AddSingleton<IDockerClientFactory, DockerClientFactory>() + 另一个
        // AddSingleton<IDockerClientCacheMaintenance, DockerClientFactory>()——那会创建两个
        // 互不相识的实例，回收任务将操作不到真正在用的那份缓存。
        services.AddSingleton<DockerClientFactory>();
        services.AddSingleton<IDockerClientFactory>(static provider => provider.GetRequiredService<DockerClientFactory>());
        services.AddSingleton<IDockerClientCacheMaintenance>(static provider => provider.GetRequiredService<DockerClientFactory>());

        // 时间源：工厂用它判定空闲，回收任务用它驱动定时器。测试可覆盖为假时间。
        services.TryAddSingleton(TimeProvider.System);

        services.AddHostedService<DockerClientCacheReaper>();

        return services;
    }
}