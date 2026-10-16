using System.Security.Claims;
using System.Text;
using Whalefleet.Application.Abstractions;
using Whalefleet.Infrastructure.DependencyInjection;
using Whalefleet.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Whalefleet.IntegrationTests.Security;

/// <summary>
/// <c>JwtTokenService</c> 的签发与自验证测试。
/// </summary>
/// <remarks>
/// <para><b>为什么要用同一套校验参数把令牌验回来</b>：只断言"字符串长得像 JWT"毫无价值——
/// 真正的契约是「Api 层的 <c>AddJwtBearer</c> 能用同一份配置验签通过」。
/// 这里用 <see cref="JsonWebTokenHandler.ValidateTokenAsync(string, TokenValidationParameters)"/>
/// 走一遍验签，就把"签发侧与验证侧的算法/issuer/audience 对得上"变成了可执行的断言。</para>
/// <para>测试只创建令牌、不启动宿主，因此密钥可以写死在测试里。</para>
/// </remarks>
public class JwtTokenServiceTests
{
    private const string _secret = "jwt-token-service-test-signing-key-0123456789";

    private static readonly DateTime _utcNow = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _userId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void CreateAccessToken_携带全部约定声明且可被同密钥验签()
    {
        var (tokens, options) = Resolve();

        var accessToken = tokens.CreateAccessToken(_userId, "admin", ["admin"], _utcNow);

        var principal = Validate(accessToken.Value, options);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be(_userId.ToString("D"));
        principal.FindFirst(JwtRegisteredClaimNames.UniqueName)?.Value.Should().Be("admin");
        principal.FindFirst(JwtClaimNames.Role)?.Value.Should().Be("admin");
        principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CreateAccessToken_过期时刻等于签发时刻加配置有效期()
    {
        var (tokens, options) = Resolve();

        var accessToken = tokens.CreateAccessToken(_userId, "admin", ["admin"], _utcNow);

        accessToken.ExpiresAt.Should().Be(_utcNow.Add(options.AccessTokenLifetime));
        accessToken.ExpiresAt.Should().Be(_utcNow.AddMinutes(15));

        // 同时校对 JWT 里的 exp 声明与返回值一致：两者不一致会让「前端算出的刷新时机」失效。
        var principal = Validate(accessToken.Value, options);
        var exp = principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        exp.Should().Be(
            new DateTimeOffset(accessToken.ExpiresAt, TimeSpan.Zero).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CreateAccessToken_两次签发的jti不同()
    {
        var (tokens, options) = Resolve();

        var first = Validate(tokens.CreateAccessToken(_userId, "admin", ["admin"], _utcNow).Value, options)
            .FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var second = Validate(tokens.CreateAccessToken(_userId, "admin", ["admin"], _utcNow).Value, options)
            .FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        first.Should().NotBeNullOrEmpty();
        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAccessToken_用户名为空_抛出参数异常(string username)
    {
        var (tokens, _) = Resolve();

        var act = () => tokens.CreateAccessToken(_userId, username, ["admin"], _utcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("admin")]
    public void CreateAccessToken_角色名为空或空白_跳过它而不是抛异常(string roleName)
    {
        // 「没有角色」是**正常状态**：新建用户到被分配角色之间存在一个窗口期，
        // 那段时间他确实一个角色都没有。若这里抛异常，那个窗口期里的用户
        // 会**登录失败**——一个连登录都做不到的中间态显然不是我们想要的。
        // 授权判定也不依赖这些声明（走 IPermissionChecker 查库），所以少一个角色
        // 只影响界面展示，不影响他实际能做什么。
        var (tokens, options) = Resolve();

        var token = tokens.CreateAccessToken(_userId, "admin", [roleName], _utcNow);
        var principal = Validate(token.Value, options);

        principal.FindFirst(JwtClaimNames.UniqueName)?.Value.Should().Be("admin");

        var roles = principal.FindAll(JwtClaimNames.Role);
        if (String.IsNullOrWhiteSpace(roleName))
        {
            roles.Should().BeEmpty();
        }
        else
        {
            roles.Should().ContainSingle().Which.Value.Should().Be("admin");
        }
    }

    [Fact]
    public void CreateAccessToken_多个角色_签成多个同名声明()
    {
        var (tokens, options) = Resolve();

        var token = tokens.CreateAccessToken(_userId, "admin", ["admin", "operator"], _utcNow);
        var principal = Validate(token.Value, options);

        // 多角色必须是**多个同名 claim**而不是一个数组 claim：
        // JwtSecurityTokenHandler 会把同名 claim 依次写进数组元素，
        // 而数组 claim 需要额外的 claim-mapping 配置，部分验证库处理并不一致。
        principal.FindAll(JwtClaimNames.Role).Select(claim => claim.Value)
            .Should().BeEquivalentTo(["admin", "operator"]);
    }

    [Fact]
    public void CreateRefreshToken_返回原文哈希与七天后的过期时刻()
    {
        var (tokens, options) = Resolve();

        var refresh = tokens.CreateRefreshToken(_utcNow);

        refresh.ExpiresAt.Should().Be(_utcNow.Add(options.RefreshTokenLifetime));
        refresh.ExpiresAt.Should().Be(_utcNow.AddDays(7));

        // 32 字节随机数的 base64url 长度固定为 43（无填充）。
        refresh.Raw.Should().HaveLength(43);
        refresh.Hash.Should().NotBe(refresh.Raw);
        refresh.Hash.Should().Be(tokens.HashRefreshToken(refresh.Raw));
    }

    [Fact]
    public void CreateRefreshToken_两次调用产生不同原文()
    {
        var (tokens, _) = Resolve();

        var first = tokens.CreateRefreshToken(_utcNow);
        var second = tokens.CreateRefreshToken(_utcNow);

        first.Raw.Should().NotBe(second.Raw);
        first.Hash.Should().NotBe(second.Hash);
    }

    [Fact]
    public void HashRefreshToken_同输入同输出且为无填充的Base64Url()
    {
        var (tokens, _) = Resolve();

        var first = tokens.HashRefreshToken("some-raw-token");
        var second = tokens.HashRefreshToken("some-raw-token");

        first.Should().Be(second);

        // SHA-256 → 32 字节 → base64url 43 字符；不得出现 '+', '/', '='。
        first.Should().HaveLength(43);
        first.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HashRefreshToken_原文为空_抛出参数异常(string rawToken)
    {
        var (tokens, _) = Resolve();

        var act = () => tokens.HashRefreshToken(rawToken);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task 不同密钥签发的令牌无法互相验证()
    {
        var (tokens, _) = Resolve();

        var accessToken = tokens.CreateAccessToken(_userId, "admin", ["admin"], _utcNow);

        var otherKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("another-completely-different-signing-key-0123"));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            accessToken.Value,
            new TokenValidationParameters
            {
                ValidIssuer = "whalefleet",
                ValidAudience = "whalefleet",
                IssuerSigningKey = otherKey,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = false,
            });

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("0123456789012345678901234567890")] // 31 字节，差一字节
    public void Secret_显式配置但不足32字节_启动校验失败(string secret)
    {
        // 显式给了却给得太短 = 把弱口令当签名密钥用，必须失败。
        // （「完全没给」是另一回事：会自动生成，见下方用例。）
        // 显式给了过短密钥时同样需要密钥存储可解析（后置配置会被构造）。
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddInfrastructure(BuildConfiguration(
            ("Jwt:Secret", secret),
            ("Persistence:DatabasePath", Path.Combine(Path.GetTempPath(), "whalefleet-jwt-fixture", "whalefleet.db"))));

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<JwtOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Secret_未配置_自动生成且落盘复用(string? secret)
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var first = ResolveIn(directory, ("Jwt:Secret", secret));
            var second = ResolveIn(directory, ("Jwt:Secret", secret));

            // 自动生成的密钥必须满足强度下限——否则校验器会挡下它，
            // 表现为「零配置启动失败」，那正是本次改动要消除的行为。
            first.Options.Secret.Should().NotBeNullOrWhiteSpace();
            Encoding.UTF8.GetByteCount(first.Options.Secret!).Should().BeGreaterThanOrEqualTo(32);

            // 核心契约：两次「启动」（两个独立的 ServiceProvider + 同一数据目录）
            // 必须拿到同一份密钥，否则重启会把所有在线用户踢下线。
            second.Options.Secret.Should().Be(first.Options.Secret);

            // 文件确实写下来了，且不在临时目录之外。
            var keyFile = Path.Combine(directory, "jwt.key");
            File.Exists(keyFile).Should().BeTrue();
            (await File.ReadAllTextAsync(keyFile)).Trim().Should().Be(first.Options.Secret);

            // 用它签发的令牌能通过同一份配置验签。
            var accessToken = first.Tokens.CreateAccessToken(_userId, "admin", ["admin"], _utcNow);
            Validate(accessToken.Value, first.Options)
                .FindFirst(JwtClaimNames.Subject)!.Value
                .Should().Be(_userId.ToString("D"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Secret_显式配置优先于磁盘上已生成的密钥()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            // 先在数据目录留下一份自动生成的密钥。
            var generated = ResolveIn(directory);
            File.Exists(Path.Combine(directory, "jwt.key")).Should().BeTrue();

            // 再显式配置：显式值必须原样生效，否则运维无法做密钥轮换或让多实例共用密钥。
            var explicitSecret = "explicitly-configured-secret-of-sufficient-length-000";
            var configured = ResolveIn(directory, ("Jwt:Secret", explicitSecret));

            configured.Options.Secret.Should().Be(explicitSecret);
            configured.Options.Secret.Should().NotBe(generated.Options.Secret);

            // 磁盘上那份不应被覆盖——否则一次临时的手工配置会毁掉持久化的密钥。
            File.ReadAllText(Path.Combine(directory, "jwt.key")).Trim()
                .Should().Be(generated.Options.Secret);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Secret_磁盘上的密钥文件为空_视为不存在并重新生成()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            // 进程可能在写入过程中被强杀，留下 0 字节文件。
            // 若把它当成有效密钥，所有令牌都会被空字符串签名——那比重新生成更糟。
            File.WriteAllText(Path.Combine(directory, "jwt.key"), String.Empty);

            var resolved = ResolveIn(directory);

            resolved.Options.Secret.Should().NotBeNullOrWhiteSpace();
            Encoding.UTF8.GetByteCount(resolved.Options.Secret!).Should().BeGreaterThanOrEqualTo(32);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Secret_自动生成的文件权限为0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows 无 POSIX 权限位，ACL 由目录继承，不在此断言。
        }

        var directory = CreateTemporaryDirectory();

        try
        {
            ResolveIn(directory);

            var mode = File.GetUnixFileMode(Path.Combine(directory, "jwt.key"));

            // 密钥只允许属主读写：同机其他用户读到它就等于能伪造任意身份。
            mode.Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "密钥文件权限必须是 0600，否则同机其他用户可读取并伪造任意用户的令牌");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "whalefleet-jwt-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static (ITokenService Tokens, JwtOptions Options) ResolveIn(
        string directory,
        params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Persistence:DatabasePath"] = Path.Combine(directory, "whalefleet.db"),
            ["Persistence:Pooling"] = "false",
        };

        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        var provider = services.BuildServiceProvider();

        return (
            provider.GetRequiredService<ITokenService>(),
            provider.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    private static (ITokenService Tokens, JwtOptions Options) Resolve()
    {
        var services = new ServiceCollection();

        // JwtSecretPostConfigure 依赖 SecretFileStore，后者依赖 IOptions<SqliteOptions>；
        // 解析 JwtOptions 因此会连带触发 Persistence 的启动校验，而 DatabasePath 是必填的。
        // 这里给一个临时路径：这些用例都显式给了密钥，密钥文件不会被真的创建。
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddInfrastructure(BuildConfiguration(
            ("Jwt:Secret", _secret),
            ("Persistence:DatabasePath", Path.Combine(Path.GetTempPath(), "whalefleet-jwt-fixture", "whalefleet.db"))));

        var provider = services.BuildServiceProvider();

        return (
            provider.GetRequiredService<ITokenService>(),
            provider.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    private static ClaimsIdentity Validate(string token, JwtOptions options)
    {
        var result = new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = options.Issuer,
                ValidAudience = options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret!)),
                ValidateIssuerSigningKey = true,

                // 刻意关闭时间校验：测试基准时刻是写死的（2026-10-13 08:00Z），
                // 与真实系统时钟无关；过期时间由测试自己断言 exp 声明来覆盖。
                ValidateLifetime = false,
            }).GetAwaiter().GetResult();

        result.IsValid.Should().BeTrue(
            "签发的令牌必须能被同一份配置验签通过，否则 Api 层的 AddJwtBearer 会拒绝它");

        return result.ClaimsIdentity;
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key,
                setting => setting.Value,
                StringComparer.Ordinal))
            .Build();
}
