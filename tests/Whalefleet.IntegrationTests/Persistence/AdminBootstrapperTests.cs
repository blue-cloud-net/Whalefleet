using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Security;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.Infrastructure.DependencyInjection;
using Whalefleet.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Whalefleet.IntegrationTests.Persistence;

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
        adminRole.Should().NotBeNull();

        // 管理员身份来自**全局分配**（host_id IS NULL），而不是用户表上的某个外键。
        // 只认全局：主机分配里的 admin 只带来该主机的 Host 域权限，不是系统管理员。
        var assignments = host.Services.GetRequiredService<IUserRoleAssignmentRepository>();
        var adminAssignments = await assignments.ListByUserAsync(admin.Id);
        adminAssignments.Should().ContainSingle();
        adminAssignments[0].RoleId.Should().Be(adminRole!.Id);
        adminAssignments[0].IsGlobal.Should().BeTrue();

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
        var assignments = host.Services.GetRequiredService<IUserRoleAssignmentRepository>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();

        // 再造一个用户，然后再次运行引导。
        var defaultRole = await host.Services.GetRequiredService<IRoleRepository>()
            .GetDefaultAsync();
        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            "someone",
            hasher.Hash(_strongPassword),
            defaultRole);

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
    public async Task 未配置凭据_自动生成密码创建管理员且落盘可登录()
    {
        await using var host = await AdminTestHost.CreateAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();

        var admin = await users.FindByUsernameAsync("admin");
        admin.Should().NotBeNull();

        // 密码写在数据目录的凭据文件里（不进日志），且必须真的能登录。
        var credentialPath = Path.Combine(host.DataDirectory, "initial-admin.txt");
        File.Exists(credentialPath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(credentialPath);
        var password = ExtractPassword(content);

        hasher.Verify(password, admin!.PasswordHash).Should().BeTrue();

        // 强度策略必须被满足——否则用户拿到密码却登不进去。
        PasswordPolicy.IsSatisfied(password).Should().BeTrue();

        // 明文密码绝不进数据库。
        admin.PasswordHash.Should().NotContain(password);
        admin.PasswordHash.Should().StartWith("$argon2id$");
    }

    [Fact]
    public async Task 未配置凭据时用户名回落为admin()
    {
        await using var host = await AdminTestHost.CreateAsync();

        (await host.Services.GetRequiredService<IUserRepository>().FindByUsernameAsync("admin"))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task 只配置用户名_沿用该用户名且自动生成密码()
    {
        // 用户名是部署方明确表达的意图，应当被尊重；只有密码必须保密，改由生成。
        await using var host = await AdminTestHost.CreateAsync(("Admin:User", "custom-admin"));

        var users = host.Services.GetRequiredService<IUserRepository>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();

        var custom = await users.FindByUsernameAsync("custom-admin");
        custom.Should().NotBeNull();

        // 密码是自动生成的，且确实能登录。
        var credentialPath = Path.Combine(host.DataDirectory, "initial-admin.txt");
        var password = ExtractPassword(await File.ReadAllTextAsync(credentialPath));
        hasher.Verify(password, custom!.PasswordHash).Should().BeTrue();

        // 默认用户名不应被创建——用户明确指定了别的名字。
        (await users.FindByUsernameAsync("admin")).Should().BeNull();
    }

    [Fact]
    public async Task 只配置密码_使用该密码且用户名回落为admin()
    {
        await using var host = await AdminTestHost.CreateAsync(("Admin:Password", _strongPassword));

        var users = host.Services.GetRequiredService<IUserRepository>();
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();

        var admin = await users.FindByUsernameAsync("admin");
        admin.Should().NotBeNull();
        hasher.Verify(_strongPassword, admin!.PasswordHash).Should().BeTrue();

        // 显式提供的密码不该被自动生成的覆盖，也就不需要写凭据文件。
        var credentialPath = Path.Combine(host.DataDirectory, "initial-admin.txt");
        File.Exists(credentialPath).Should().BeFalse();
    }

    [Fact]
    public async Task 自动生成的密码不落日志()
    {
        // 密码只允许出现在数据目录的凭据文件里。若哪天有人图省事加了一行
        // LogInformation("{Password}", password)，这条测试就会挡住它。
        await using var host = await AdminTestHost.CreateAsync();

        var admin = await host.Services.GetRequiredService<IUserRepository>().FindByUsernameAsync("admin");

        var credentialPath = Path.Combine(host.DataDirectory, "initial-admin.txt");
        var password = ExtractPassword(await File.ReadAllTextAsync(credentialPath));

        host.CapturedLogs.Should().NotContain(password);
        host.CapturedLogs.Should().Contain("initial-admin.txt");
    }

    [Fact]
    public async Task 用户表已有账号时不生成凭据文件()
    {
        await using var host = await AdminTestHost.CreateAsync(
            ("Admin:User", "admin"),
            ("Admin:Password", _strongPassword));

        var credentialPath = Path.Combine(host.DataDirectory, "initial-admin.txt");
        File.Exists(credentialPath).Should().BeFalse();
    }

    /// <summary>
    /// 从凭据文件中取出 <c>ADMIN__PASSWORD</c> 的值。
    /// </summary>
    private static string ExtractPassword(string credentialContent)
    {
        var line = credentialContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(candidate => candidate.StartsWith("ADMIN__PASSWORD=", StringComparison.Ordinal));

        return line["ADMIN__PASSWORD=".Length..].Trim();
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
    /// 引导测试宿主：真实 SQLite + 真实 Argon2，并在构造时依次执行数据库初始化与管理员引导。
    /// </summary>
    private sealed class AdminTestHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _directory;

        private AdminTestHost(ServiceProvider provider, string directory, string capturedLogs)
        {
            _provider = provider;
            _directory = directory;
            CapturedLogs = capturedLogs;
        }

        internal IServiceProvider Services => _provider;

        /// <summary>数据目录（密钥与凭据文件所在处）。</summary>
        internal string DataDirectory => _directory;

        /// <summary>引导期间捕获的全部日志文本。</summary>
        internal string CapturedLogs { get; }

        internal static async Task<AdminTestHost> CreateAsync(params (string Key, string Value)[] settings)
        {
            var directory = Path.Combine(Path.GetTempPath(), "whalefleet-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Persistence:DatabasePath"] = Path.Combine(directory, "whalefleet.db"),
                ["Persistence:CommandTimeoutSeconds"] = "30",

                // 关掉连接池：清池是进程级操作，会踩到并行测试正在用的连接句柄；
                // 关掉后连接用完即释放，临时目录也能直接删。
                ["Persistence:Pooling"] = "false",

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

            var recorder = new CapturingLoggerProvider();

            var services = new ServiceCollection();
            services.AddSingleton(recorder);
            services.AddSingleton<ILoggerFactory>(recorder);
            services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
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
                Directory.Delete(directory, recursive: true);
                throw;
            }

            return new AdminTestHost(provider, directory, recorder.Text);
        }

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

    /// <summary>
    /// 把日志写入内存，供「密码不得进日志」这类断言检查。
    /// </summary>
    /// <remarks>
    /// 刻意在格式化消息之外<b>额外记录一次 <c>state.ToString()</c></b>：
    /// 那正是「把 <c>LogInformation("…{Options}", options)</c> 这类整对象打出来」的常见写法，
    /// 若只记格式化后的消息，结构化属性里的密码会漏过本测试。
    /// </remarks>
    public sealed class CapturingLoggerProvider : ILoggerFactory
    {
        private readonly List<string> _lines = [];

        internal string Text => String.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName) => new CoreCapturingLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        internal void Write<TState>(
            Func<TState, Exception?, string> formatter,
            TState state,
            Exception? exception)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            ArgumentNullException.ThrowIfNull(state);

            _lines.Add(formatter(state, exception));

            // 污染日志：让「整个 state 对象被序列化进日志」这种写法也能被断言捕获。
            _lines.Add(state.ToString() ?? String.Empty);
        }
    }

    /// <summary>
    /// 非泛型的日志核心。两个泛型/非泛型外壳共享同一份缓冲。
    /// </summary>
    public sealed class CoreCapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _provider;

        internal CoreCapturingLogger(CapturingLoggerProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _provider.Write(formatter, state, exception);
    }

    /// <summary>
    /// DI 注册 <c>ILogger&lt;T&gt;</c> 时使用的泛型外壳；非泛型核心持有同一份行缓冲。
    /// </summary>
    /// <remarks>
    /// 构造必须是 <b>public</b>：DI 只挑 public 构造，internal 会被当成「没有可用构造」而报
    /// "A suitable constructor ... could not be located"。
    /// </remarks>
    public sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly CapturingLoggerProvider _provider;

        public CapturingLogger(CapturingLoggerProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _provider.Write(formatter, state, exception);
    }
}
