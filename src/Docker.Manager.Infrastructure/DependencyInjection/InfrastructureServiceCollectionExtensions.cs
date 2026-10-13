using Docker.Manager.Application.Abstractions;
using Docker.Manager.Core.Auth;
using Docker.Manager.Infrastructure.Docker;
using Docker.Manager.Infrastructure.Security;
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
    /// - <see cref="IDockerClientFactory"/> → <see cref="DockerClientFactory"/>（Singleton，无状态）
    /// - <see cref="IDockerClientPool"/> / <see cref="IDockerClientPoolMaintenance"/> /
    ///   <see cref="DockerClientPool"/>（Singleton，三者是<b>同一实例</b>）
    /// - <see cref="IDockerHostAccessor"/> → <see cref="DockerHostAccessor"/>（Scoped）
    /// - <see cref="DockerClientPoolReaper"/>（后台空闲回收）
    /// - <see cref="TimeProvider"/>（若调用方尚未注册）
    /// </summary>
    /// <remarks>
    /// <para><b>池为什么必须是单例</b>：客户端要跨请求存活，否则「切换主机不重建连接」
    /// 就退化成了每次请求重连。</para>
    /// <para><b>访问器为什么是 Scoped</b>：它依赖 Scoped 的主机仓储（其取值来自当前请求）。
    /// 池只接受连接描述、不查数据库，因此以单例注册不会构成「捕获依赖」。</para>
    /// <para><b>配置校验</b>：池策略（空闲超时 / 扫描间隔）非法会在<b>启动时</b>失败
    /// （<c>ValidateOnStart</c>）。主机<b>不</b>来自配置——它们是 <c>hosts</c> 表里的数据。</para>
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
                options => options.Pool.IdleTimeout >= TimeSpan.Zero
                    && options.Pool.ReapInterval >= TimeSpan.Zero,
                "Docker:Pool 的 IdleTimeout / ReapInterval 不能为负值")
            .ValidateOnStart();

        // 工厂是无状态的纯构建器，单独注册即可。
        services.AddSingleton<IDockerClientFactory, DockerClientFactory>();

        // 池本体只注册一次，再以三个身份暴露同一实例：
        // 业务侧用 IDockerClientPool，后台回收任务用 IDockerClientPoolMaintenance，
        // 直接构造（如测试）用具体类型。**不能**写成多个 AddSingleton<TInterface, DockerClientPool>()
        // ——那会创建多个互不相识的实例，回收任务将操作不到真正在用的那份池。
        services.AddSingleton<DockerClientPool>();
        services.AddSingleton<IDockerClientPool>(static provider => provider.GetRequiredService<DockerClientPool>());
        services.AddSingleton<IDockerClientPoolMaintenance>(static provider => provider.GetRequiredService<DockerClientPool>());

        // 时间源：池用它判定空闲，回收任务用它驱动定时器。测试可覆盖为假时间。
        services.TryAddSingleton(TimeProvider.System);

        services.AddHostedService<DockerClientPoolReaper>();

        // 主机访问器是 Scoped（依赖 Scoped 的主机仓储）；池是 Singleton——
        // 二者生命周期不同，故「解析主机」与「池化客户端」分属两层。
        services.AddScoped<IDockerHostAccessor, DockerHostAccessor>();

        // 密码哈希（Argon2id）：参数越界在启动时即失败（ValidateOnStart），
        // 而不是拖到第一次登录。哈希器无状态，故为 Singleton。
        services
            .AddOptions<Argon2Options>()
            .Bind(configuration.GetSection(Argon2Options.SectionName))
            .Validate(
                static options => IsValidArgon2Options(options),
                "Security:Argon2 参数越界：内存须 ≥ 8 × 并行度，各字段须在 PasswordHash 允许的范围内")
            .ValidateOnStart();

        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

        // 持久化（SQLite + Dapper）：连接工厂、架构迁移、仓储。
        // 数据库的实际初始化（迁移 / WAL / 种子）由宿主在启动阶段显式调用
        // IDatabaseInitializer，不在 DI 注册期执行。
        services.AddPersistence(configuration);

        return services;
    }

    /// <summary>
    /// 校验 Argon2 参数是否合法。
    /// </summary>
    /// <remarks>
    /// <para>刻意<b>复用</b> <see cref="PasswordHash.Create"/> 的校验规则，而不是在此重写一套范围判断——
    /// 两处规则一旦漂移，就会出现「启动校验通过、首次登录却抛异常」的错位。</para>
    /// <para>先用显式边界拦住长度字段，避免极小/极大的长度值在构造探测数组时先抛
    /// <see cref="OverflowException"/>（那不属于参数越界的语义）。</para>
    /// </remarks>
    /// <param name="options">待校验的选项。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    private static bool IsValidArgon2Options(Argon2Options options)
    {
        if (options.SaltSizeBytes is < PasswordHash.MinSaltLength or > PasswordHash.MaxSaltLength
            || options.HashSizeBytes is < PasswordHash.MinDigestLength or > PasswordHash.MaxDigestLength)
        {
            return false;
        }

        try
        {
            // 返回值只是用来触发一次完整校验，不参与任何计算。
            _ = PasswordHash.Create(
                options.MemorySizeKiB,
                options.Iterations,
                options.Parallelism,
                new byte[options.SaltSizeBytes],
                new byte[options.HashSizeBytes]);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}