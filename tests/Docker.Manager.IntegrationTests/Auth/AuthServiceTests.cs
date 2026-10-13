using Docker.Manager.Application.Auth;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Auth;

/// <summary>
/// <c>AuthService</c> 的登录编排与安全语义测试。
/// </summary>
/// <remarks>
/// <para>用<b>真实</b> SQLite + 真实 Argon2 + 真实 JWT 跑完整登录链路——因为这里要验证的
/// 恰恰是"编排"：查库（不区分大小写）、校验、查角色、签发、落库。用桩替换其中任何一环，
/// 就验证不到「仓储的 NOCASE 语义」与「令牌确实落到了 <c>refresh_tokens</c>」。</para>
/// <para>其中两条是<b>安全断言</b>，注释里单独标注：响应不可区分、耗时不可区分。</para>
/// </remarks>
public class AuthServiceTests
{
    private const string _ip = "203.0.113.7";

    [Fact]
    public async Task LoginAsync_正确凭据_返回令牌并落库刷新令牌哈希()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        var userId = await host.CreateUserAsync("admin");

        var response = await host.CreateAuthService().LoginAsync(
            new LoginRequest { Username = "admin", Password = AuthTestHost.Password },
            _ip);

        response.AccessToken.Should().NotBeNullOrWhiteSpace();
        response.RefreshToken.Should().NotBeNullOrWhiteSpace();
        response.ExpiresIn.Should().Be(900);
        response.User.Id.Should().Be(userId);
        response.User.Username.Should().Be("admin");
        response.User.Role.Should().Be("admin");

        // 落库的是哈希，而不是返回给客户端的原文。
        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        var stored = await tokens.FindByHashAsync(
            host.Services.GetRequiredService<Docker.Manager.Application.Abstractions.ITokenService>()
                .HashRefreshToken(response.RefreshToken));

        stored.Should().NotBeNull();
        stored!.UserId.Should().Be(userId);
        stored.TokenHash.Should().NotBe(response.RefreshToken);
        stored.ExpiresAt.Should().Be(AuthTestHost.Now.UtcDateTime.AddDays(7));
        stored.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task LoginAsync_用户名不区分大小写_仍可登录()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");

        var response = await host.CreateAuthService().LoginAsync(
            new LoginRequest { Username = "ADMIN", Password = AuthTestHost.Password },
            _ip);

        response.User.Username.Should().Be("admin", "响应应回显库中保存的大小写");
    }

    [Fact]
    public async Task LoginAsync_用户名含首尾空白_仍可登录()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");

        var response = await host.CreateAuthService().LoginAsync(
            new LoginRequest { Username = "  admin  ", Password = AuthTestHost.Password },
            _ip);

        response.User.Username.Should().Be("admin");
    }

    [Fact]
    public async Task LoginAsync_密码错误_抛出凭据错误()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");

        var exception = await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");

        exception.Code.Should().Be(ErrorCodes.Unauthenticated);
    }

    /// <summary>
    /// 【安全断言】用户名不存在与密码错误必须<b>无法区分</b>。
    /// </summary>
    [Fact]
    public async Task LoginAsync_用户不存在_与密码错误的响应完全一致()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");

        var wrongPassword = await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");
        var unknownUser = await AssertLoginFailsAsync(host, "no-such-user", AuthTestHost.Password);

        unknownUser.Code.Should().Be(wrongPassword.Code);
        unknownUser.Message.Should().Be(wrongPassword.Message);
    }

    /// <summary>
    /// 【安全断言】用户名不存在时也必须计入失败次数。
    /// </summary>
    /// <remarks>
    /// 否则攻击者可以靠"是否被限流"反推出用户名是否存在，让防枚举的努力前功尽弃。
    /// 这条同时是"用户不存在也走哑元校验"这一时序防护的<b>可观测代理</b>——
    /// 真实耗时无法在测试里可靠断言，但"失败被等价计数"能证明两条路径走的是同一套逻辑。
    /// </remarks>
    [Fact]
    public async Task LoginAsync_用户不存在也计入失败次数_达到上限后同样被限流()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();

        for (var i = 0; i < 5; i++)
        {
            await AssertLoginFailsAsync(host, "no-such-user", AuthTestHost.Password);
        }

        var exception = await AssertLoginFailsAsync(host, "no-such-user", AuthTestHost.Password);

        exception.Code.Should().Be(ErrorCodes.LoginRateLimited);
    }

    [Fact]
    public async Task LoginAsync_连续失败达上限_抛出限流错误()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");

        for (var i = 0; i < 5; i++)
        {
            await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");
        }

        var exception = await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");
        exception.Code.Should().Be(ErrorCodes.LoginRateLimited);

        // 即使这时给出正确密码，也应被拦（限流先于校验）。
        var withCorrectPassword = await AssertLoginFailsAsync(host, "admin", AuthTestHost.Password);
        withCorrectPassword.Code.Should().Be(ErrorCodes.LoginRateLimited);
    }

    [Fact]
    public async Task LoginAsync_登录成功后清零失败计数()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");
        var service = host.CreateAuthService();

        await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");
        await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");

        await service.LoginAsync(
            new LoginRequest { Username = "admin", Password = AuthTestHost.Password },
            _ip);

        // 计数已清零：再失败 4 次仍不应触发限流（上限为 5）。
        for (var i = 0; i < 4; i++)
        {
            var exception = await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");
            exception.Code.Should().Be(ErrorCodes.Unauthenticated);
        }
    }

    [Fact]
    public async Task LoginAsync_限流窗口过期后_可重新登录()
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();
        await host.CreateUserAsync("admin");

        for (var i = 0; i < 5; i++)
        {
            await AssertLoginFailsAsync(host, "admin", "WrongP@ss123");
        }

        host.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        var response = await host.CreateAuthService().LoginAsync(
            new LoginRequest { Username = "admin", Password = AuthTestHost.Password },
            _ip);

        response.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null, AuthTestHost.Password)]
    [InlineData("", AuthTestHost.Password)]
    [InlineData("   ", AuthTestHost.Password)]
    [InlineData("admin", null)]
    [InlineData("admin", "")]
    public async Task LoginAsync_用户名或密码为空_抛出参数校验失败(string? username, string? password)
    {
        await using var host = new AuthTestHost();
        await host.InitializeAsync();

        var act = async () => await host.CreateAuthService().LoginAsync(
            new LoginRequest { Username = username, Password = password },
            _ip);

        var exception = await act.Should().ThrowAsync<BusinessException>();

        // 注意与"凭据错误"的区别：缺字段是 1000（参数校验），不是 1100（认证失败）。
        exception.Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// 断言一次登录失败，并返回异常。
    /// </summary>
    private static async Task<BusinessException> AssertLoginFailsAsync(
        AuthTestHost host,
        string username,
        string password)
    {
        var act = async () => await host.CreateAuthService().LoginAsync(
            new LoginRequest { Username = username, Password = password },
            _ip);

        var exception = await act.Should().ThrowAsync<BusinessException>();

        return exception.Which;
    }
}
