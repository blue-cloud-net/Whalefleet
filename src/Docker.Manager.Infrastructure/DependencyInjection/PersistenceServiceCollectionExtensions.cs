using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Core.Auth;
using Docker.Manager.Core.Hosts;
using Docker.Manager.Core.Users;
using Docker.Manager.Infrastructure.Persistence;
using Docker.Manager.Infrastructure.Persistence.Auth;
using Docker.Manager.Infrastructure.Persistence.Hosts;
using Docker.Manager.Infrastructure.Persistence.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docker.Manager.Infrastructure.DependencyInjection;

/// <summary>
/// 持久化层服务注册扩展（SQLite + Dapper）。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：连接工厂、迁移器、初始化器都是单例（无请求状态，且缓存成本高）；
/// 仓储是 <b>Scoped</b>——它们依赖 <see cref="ICurrentUser"/>，后者的取值来自当前请求。</para>
/// <para><b><see cref="ICurrentUser"/> 用 <c>TryAdd</c></b>：0.0.3 先给出「无登录用户」的占位实现，
/// 0.0.4 认证落地后由 Api 层用 <c>IServiceCollection.Replace</c> 覆盖。
/// 注意 <c>TryAdd</c> <b>不会</b>覆盖已注册项，因此覆盖必须用 <c>Replace</c>。</para>
/// <para><b>配置校验</b>：数据库路径为空、超时非正数都在<b>启动时</b>失败（<c>ValidateOnStart</c>），
/// 而不是拖到第一次查询。</para>
/// </remarks>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// 注册持久化层服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（读取 <c>Persistence</c> 配置节）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SqliteOptions>()
            .Bind(configuration.GetSection(SqliteOptions.SectionName))
            .Validate(
                options => !String.IsNullOrWhiteSpace(options.DatabasePath),
                "Persistence:DatabasePath 不能为空")
            .Validate(
                options => options.CommandTimeoutSeconds > 0,
                "Persistence:CommandTimeoutSeconds 必须大于 0")
            .ValidateOnStart();

        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<SqliteSchemaMigrator>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();

        // 0.0.3 尚无认证：审计人一律落到默认值哨兵（Guid.Empty = 系统操作）。
        services.TryAddSingleton<ICurrentUser, SystemCurrentUser>();

        services.AddScoped<IRoleRepository, SqliteRoleRepository>();
        services.AddScoped<IUserRepository, SqliteUserRepository>();
        services.AddScoped<IHostRepository, SqliteHostRepository>();
        services.AddScoped<IHostGroupRepository, SqliteHostGroupRepository>();
        services.AddScoped<IRefreshTokenRepository, SqliteRefreshTokenRepository>();

        return services;
    }
}
