using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Endpoints;

/// <summary>
/// 刷新令牌轮换、重放检测与登出的端到端测试。
/// </summary>
/// <remarks>
/// <para>这里守的是 0.0.5 的核心安全语义：<b>刷新令牌一次性</b>。
/// 只测「刷新能拿到新令牌」是不够的——真正的契约是「旧令牌立刻就不好使了」，
/// 以及「旧令牌被重放时，整个令牌族都要作废」。</para>
/// <para>每个测试用独立宿主：登录失败计数与限流窗口都是进程内状态。</para>
/// </remarks>
// 释放入口是 IAsyncLifetime.DisposeAsync，而 CA1001 只认同步 IDisposable 模式。
// 抑制必须写在类声明之前：诊断报在类型声明那一行，写在类体内对它无效（这坑踩过一次）。
#pragma warning disable CA1001
public class AuthRefreshLogoutTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
#pragma warning restore CA1001

    private const string _password = "StrongP@ss123";
    private const string _username = "admin";

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();

        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync("admin");
        await users.AddAsync(
            User.Create(Guid.NewGuid(), _username, hasher.Hash(_password), adminRole!.Id));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 刷新成功_返回新令牌对且两个令牌都与旧的不同()
    {
        using var client = _factory.CreateClient();
        var login = await LoginAsync(client);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var refreshed = ReadData<LoginData>(await ReadAsync(response));

        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshed.RefreshToken.Should().NotBe(login.RefreshToken, "刷新必须轮换刷新令牌");
        refreshed.User.Username.Should().Be(_username);
        refreshed.ExpiresIn.Should().Be(900);
    }

    [Fact]
    public async Task 已轮换的旧令牌再次使用_判定为重放并作废整个令牌族()
    {
        using var client = _factory.CreateClient();
        var login = await LoginAsync(client);

        // 第一次刷新：旧令牌被轮换，拿到新的刷新令牌 r2。
        using var firstRefresh = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });

        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = ReadData<LoginData>(await ReadAsync(firstRefresh));

        // 重放旧令牌：应当被拒，并触发"作废该用户全部刷新令牌"。
        using var replay = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadAsync(replay)).Code.Should().Be(ErrorCodes.RefreshTokenRevoked);

        // 关键断言：连同轮换产生的新令牌也一并失效（否则"重放检测"只是拒绝了一次请求而已）。
        using var afterReplay = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken });

        afterReplay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadAsync(afterReplay)).Code.Should().Be(ErrorCodes.RefreshTokenRevoked);
    }

    [Fact]
    public async Task 已登出的令牌再次使用_只拒绝而不牵连其他会话()
    {
        using var client = _factory.CreateClient();

        // 两个会话（两次登录）。
        var firstSession = await LoginAsync(client);
        var secondSession = await LoginAsync(client);

        using var logout = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = firstSession.RefreshToken });

        logout.StatusCode.Should().Be(HttpStatusCode.OK);

        // 用已登出的令牌刷新 → 拒绝。
        using var withLoggedOut = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = firstSession.RefreshToken });

        withLoggedOut.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadAsync(withLoggedOut)).Code.Should().Be(ErrorCodes.RefreshTokenRevoked);

        // 另一个会话不受影响——这是"登出"与"重放"必须区别对待的原因。
        using var withOtherSession = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = secondSession.RefreshToken });

        withOtherSession.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 未知令牌_返回1102()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = "not-a-real-refresh-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync(response);
        body.Code.Should().Be(ErrorCodes.RefreshTokenRevoked);
        body.Data.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 刷新与登出_缺少刷新令牌_返回400与参数校验错误(string? refreshToken)
    {
        using var client = _factory.CreateClient();

        // refresh 与 logout 共用同一份输入契约，因此一并断言。
        foreach (var endpoint in new[] { "refresh", "logout" })
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/auth/{endpoint}",
                new { refreshToken });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{endpoint} 应拒绝空令牌");
            (await ReadAsync(response)).Code.Should().Be(ErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task 登出_成功且幂等()
    {
        using var client = _factory.CreateClient();
        var login = await LoginAsync(client);

        // 第一次登出：真正撤销。
        using var first = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = login.RefreshToken });

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // 第二次登出：令牌已撤销，仍应成功（登出是清理动作，报错只会让客户端绕圈子）。
        using var second = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = login.RefreshToken });

        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(second);
        body.Success.Should().BeTrue();
        body.Code.Should().Be(ErrorCodes.Success);
    }

    [Fact]
    public async Task 登出不存在的令牌_同样返回成功()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = "never-existed" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 登录并返回令牌对。
    /// </summary>
    private static async Task<LoginData> LoginAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _username, password = _password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return ReadData<LoginData>(await ReadAsync(response));
    }

    private static async Task<ApiResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResponse>())!;

    private static T ReadData<T>(ApiResponse body) =>
        body.Data!.Value.Deserialize<T>(JsonSerializerOptions.Web)!;

    private sealed record LoginData(string AccessToken, string RefreshToken, int ExpiresIn, UserData User);

    private sealed record UserData(Guid Id, string Username, string Role);
}
