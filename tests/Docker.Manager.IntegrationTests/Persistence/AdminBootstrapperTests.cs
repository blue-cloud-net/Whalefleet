using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Core.Users;
using Docker.Manager.Infrastructure.DependencyInjection;
using Docker.Manager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Persistence;

/// <summary>
/// <c>AdminBootstrapper</c> 的引导行为测试（真实 SQLite + 真实 Argon2）。
/// </summary>
/// <remarks>
/// 引导只该在「系统里还没有任何账号」时生效，因此本组测试的重点全是<b>条件</b>：
/// 什么情况下建、什么情况下不建、什么情况下必须让启动失败。
/// </remarks>
public class AdminBootstrapperTests
{
    private const string _strongPassword = "AdminP@ssw0rd1";

    [Fact]
    public async Task users表为空且配置齐全_创建可用密码登录的管理员()
    {
        await using var host = await AdminTestHost.CreateAsync(
            ("Admin:User", "admin"),
            ("Admin:Password", _strongPassword));

        var users = host.Services.GetRequiredService<IUserRepository>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var admin = await users.FindByUsernameAsync("admin");

        admin.Should().NotBeNull();
        hasher.Verify(_strongPassword, admin!.PasswordHash).Should().BeTrue();

        var adminRole = await roles.FindByNameAsync("admin");
        admin.RoleId.Should().Be(adminRole!.Id);

        // 密码绝不落明文。
        admin.PasswordHash.Should().NotContain(_strongPassword);
        admin.PasswordHash.Should().StartWith("$argon2id$");
    }

    [Fact]
    public async Task users表非空_不重复创建()
    {
        await using var host = await AdminTestHost.CreateAsync(
            ("Admin:User", "admin"),
            ("Admin:Password", _strongPassword));

        var users = host.Services.GetRequiredService<IUserRepository>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();

        // 再造一个用户，然后再次运行引导。
        var defaultRole = await host.Services.GetRequiredService<IRoleRepository>()
            .GetDefaultAsync();
        await users.AddAsync(User.Create(Guid.NewGuid(), "someone", hasher.Hash(_strongPassword), defaultRole!.Id));

        await host.Services.GetRequiredService<IAdminBootstrapper>().InitializeAsync();

        (await users.FindByUsernameAsync("admin")).Should().NotBeNull();
        (await users.ListAsync(0, 10)).Should().HaveCount(2);
    }

    [Fact]
    public async Task 用户被软删除后再次引导_不重复创建()
    {
        await using var host = await AdminTestHost.CreateAsync(
            ("Admin:User", "admin"),
            ("Admin:Password", _strongPassword));

        var users = host.Services.GetRequiredService<IUserRepository>();
        var admin = await users.FindByUsernameAsync("admin");

        // 软删除后业务计数为 0，但物理行仍在——若按业务计数判空就会重复种入
        // 并与残留行撞部分唯一索引，把启动搞崩。
        await users.RemoveByIdAsync(admin!.Id);

        await host.Services.GetRequiredService<IAdminBootstrapper>().InitializeAsync();

        // 物理行数仍为 1，因此引导应当跳过（不会因唯一索引冲突而抛异常）。
        (await users.QueryAllListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 未配置凭据_不创建且不抛异常()
    {
        await using var host = await AdminTestHost.CreateAsync();

        (await host.Services.GetRequiredService<IUserRepository>().QueryAllListAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task 只配置用户名_视为未配置()
    {
        await using var host = await AdminTestHost.CreateAsync(("Admin:User", "admin"));

        (await host.Services.GetRequiredService<IUserRepository>().QueryAllListAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task 只配置密码_视为未配置()
    {
        await using var host = await AdminTestHost.CreateAsync(("Admin:Password", _strongPassword));

        (await host.Services.GetRequiredService<IUserRepository>().QueryAllListAsync())
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("short1A")] // 长度不足
    [InlineData("alllettersonly")] // 缺数字/符号
    [InlineData("1234567890")] // 缺字母
    public async Task 密码不满足强度_抛出无效操作异常(string password)
    {
        // 这是系统里还没有任何账号的唯一时机，放行弱密码等于把门开着不管。
        var act = async () => await AdminTestHost.CreateAsync(
            ("Admin:User", "admin"),
            ("Admin:Password", password));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*密码*");
    }

    [Theory]
    [InlineData("ab")] // 过短
    [InlineData("has space")] // 含非法字符
    [InlineData("bad/name")]
    public async Task 用户名非法_抛出异常(string username)
    {
        var act = async () => await AdminTestHost.CreateAsync(
            ("Admin:User", username),
            ("Admin:Password", _strongPassword));

        await act.Should().ThrowAsync<Exception>();
    }

    /// <summary>
    /// 引导测试宿主：真实 SQLite + 真实 Argon2，并在构造时依次执行数据库初始化与管理員引导。
    /// </summary>
    private sealed class AdminTestHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _directory;

        private AdminTestHost(ServiceProvider provider, string directory)
        {
            _provider = provider;
            _directory = directory;
        }

        internal IServiceProvider Services => _provider;

        internal static async Task<AdminTestHost> CreateAsync(params (string Key, string Value)[] settings)
        {
            var directory = Path.Combine(Path.GetTempPath(), "docker-manager-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Persistence:DatabasePath"] = Path.Combine(directory, "docker-manager.db"),
                ["Persistence:CommandTimeoutSeconds"] = "30",

                // 引导测试不关心 KDF 强度，用便宜参数。
                ["Security:Argon2:MemorySizeKiB"] = "8192",
                ["Security:Argon2:Iterations"] = "1",
                ["Security:Argon2:Parallelism"] = "1",
            };

            foreach (var (key, value) in settings)
            {
                values[key] = value;
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddInfrastructure(configuration);

            var provider = services.BuildServiceProvider();

            try
            {
                await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
                await provider.GetRequiredService<IAdminBootstrapper>().InitializeAsync();
            }
            catch
            {
                provider.Dispose();
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
                throw;
            }

            return new AdminTestHost(provider, directory);
        }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
