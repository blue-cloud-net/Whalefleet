using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Auth;
using Whalefleet.Application.DependencyInjection;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.Infrastructure.DependencyInjection;
using Whalefleet.Infrastructure.Persistence;
using Whalefleet.IntegrationTests.Infrastructure;
using Whalefleet.IntegrationTests.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whalefleet.IntegrationTests.Auth;

/// <summary>
/// 认证测试宿主：真实 SQLite + 真实 Argon2（便宜参数）+ 真实 JWT，外加可控时钟。
/// </summary>
/// <remarks>
/// <para><b>为什么不复用 <c>PersistenceTestHost</c></b>：那个只装了持久化层，而认证用例还需要
/// 密码哈希器、令牌服务与限流器（都在 <c>AddInfrastructure</c> 里）。与其让它承担两种职责，
/// 不如各装各的——两个宿主的差异本身就说明了各自的测试关注点。</para>
/// <para><b>为什么用便宜的 Argon2 参数</b>：本组测试断言的是<b>编排与安全语义</b>
/// （响应是否可区分、失败是否计数、令牌是否落库），而非 KDF 强度——后者由
/// <c>Argon2PasswordHasherTests</c> 用默认参数覆盖。</para>
/// </remarks>
internal sealed class AuthTestHost : IAsyncDisposable
{
    /// <summary>
    /// 所有测试用户共用的明文密码。
    /// </summary>
    internal const string Password = "StrongP@ss123";

    /// <summary>
    /// 测试基准时刻（UTC）。
    /// </summary>
    internal static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private const string _jwtSecret = "auth-service-test-signing-key-0123456789abcdef";

    private readonly ServiceProvider _provider;
    private readonly string _directory;

    /// <summary>
    /// 初始化测试宿主。
    /// </summary>
    internal AuthTestHost()
    {
        _directory = Path.Combine(Path.GetTempPath(), "whalefleet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        this.DatabasePath = Path.Combine(_directory, "whalefleet.db");
        this.TimeProvider = new TestTimeProvider(Now);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Persistence:DatabasePath"] = this.DatabasePath,
                ["Persistence:CommandTimeoutSeconds"] = "30",

                // 关掉连接池：清池是进程级操作，会踩到并行测试正在用的连接句柄（见类型备注）。
                ["Persistence:Pooling"] = "false",
                ["Jwt:Secret"] = _jwtSecret,

                // 便宜的 Argon2 参数（见类型备注）。
                ["Security:Argon2:MemorySizeKiB"] = "8192",
                ["Security:Argon2:Iterations"] = "1",
                ["Security:Argon2:Parallelism"] = "1",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // 先注册：AddInfrastructure / AddPersistence 内的 TryAdd 不会覆盖已存在的注册。
        services.AddSingleton<TimeProvider>(this.TimeProvider);
        services.AddSingleton<ICurrentUser>(new StubCurrentUser(null));

        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);

        _provider = services.BuildServiceProvider();
    }

    /// <summary>
    /// 临时数据库文件路径。
    /// </summary>
    internal string DatabasePath { get; }

    /// <summary>
    /// 可控时钟（用于推进限流窗口）。
    /// </summary>
    internal TestTimeProvider TimeProvider { get; }

    /// <summary>
    /// 服务定位器。
    /// </summary>
    internal IServiceProvider Services => _provider;

    /// <summary>
    /// 执行数据库初始化（迁移 + 内置角色 + 默认主机种子）。
    /// </summary>
    /// <returns>初始化任务。</returns>
    internal Task InitializeAsync() =>
        _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

    /// <summary>
    /// 创建一个用户，密码统一为 <see cref="Password"/>。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="roleName">角色名（须为迁移种入的内置角色，如 <c>admin</c>）。</param>
    /// <returns>新用户的主键。</returns>
    internal async Task<Guid> CreateUserAsync(string username, string roleName = "admin")
    {
        var roles = _provider.GetRequiredService<IRoleRepository>();
        var users = _provider.GetRequiredService<IUserRepository>();
        var assignments = _provider.GetRequiredService<IUserRoleAssignmentRepository>();
        var hasher = _provider.GetRequiredService<IPasswordHasher>();

        var role = await roles.FindByNameAsync(roleName);
        role.Should().NotBeNull($"内置角色 {roleName} 应由迁移种入");

        // 身份来自**全局分配**而不是用户表上的外键（0.1.0 起）。
        // 少这一步的话账号能登录，但响应里的 Roles 恒为空——看起来像登录功能坏了。
        return await TestAccounts.CreateUserAsync(
            users,
            assignments,
            username,
            hasher.Hash(Password),
            role);
    }

    /// <summary>
    /// 从容器解析认证服务。
    /// </summary>
    /// <returns>认证服务实例。</returns>
    internal AuthService CreateAuthService() => _provider.GetRequiredService<AuthService>();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _provider.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
