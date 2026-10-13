using System.Security.Claims;
using System.Text;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Infrastructure.DependencyInjection;
using Docker.Manager.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Docker.Manager.IntegrationTests.Security;

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

        var accessToken = tokens.CreateAccessToken(_userId, "admin", "admin", _utcNow);

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

        var accessToken = tokens.CreateAccessToken(_userId, "admin", "admin", _utcNow);

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

        var first = Validate(tokens.CreateAccessToken(_userId, "admin", "admin", _utcNow).Value, options)
            .FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var second = Validate(tokens.CreateAccessToken(_userId, "admin", "admin", _utcNow).Value, options)
            .FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        first.Should().NotBeNullOrEmpty();
        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData("", "admin")]
    [InlineData("   ", "admin")]
    [InlineData("admin", "")]
    public void CreateAccessToken_用户名或角色名为空_抛出参数异常(string username, string roleName)
    {
        var (tokens, _) = Resolve();

        var act = () => tokens.CreateAccessToken(_userId, username, roleName, _utcNow);

        act.Should().Throw<ArgumentException>();
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

        var accessToken = tokens.CreateAccessToken(_userId, "admin", "admin", _utcNow);

        var otherKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("another-completely-different-signing-key-0123"));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            accessToken.Value,
            new TokenValidationParameters
            {
                ValidIssuer = "docker-manager",
                ValidAudience = "docker-manager",
                IssuerSigningKey = otherKey,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = false,
            });

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("0123456789012345678901234567890")] // 31 字节，差一字节
    [InlineData("")]
    [InlineData(null)]
    public void Secret_缺失或不足32字节_启动校验失败(string? secret)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(BuildConfiguration(("Jwt:Secret", secret)));

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<JwtOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    private static (ITokenService Tokens, JwtOptions Options) Resolve()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(BuildConfiguration(("Jwt:Secret", _secret)));

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
