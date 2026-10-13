using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Docker.Manager.IntegrationTests.Endpoints;

/// <summary>
/// 认证端点的端到端测试（登录、当前用户、鉴权失败形态）。
/// </summary>
/// <remarks>
/// <para><b>每个测试用独立宿主</b>（<see cref="IAsyncLifetime"/>）：登录失败计数是<b>进程内</b>的，
/// 共享宿主会让「第 6 次触发限流」这类用例互相污染，也让失败计数断言失去意义。</para>
/// <para>本组同时守住 0.0.4 的两条安全契约：</para>
/// <list type="number">
///   <item><description>凭据错误时「用户名不存在」与「密码错误」的响应<b>完全一致</b>。</description></item>
///   <item><description>鉴权失败时「未认证（1100）」与「已过期（1101）」<b>可区分</b>——
///   这是 0.0.5 的 401 → refresh 重发得以成立的前提。</description></item>
/// </list>
/// </remarks>
// CA1001：本类型持有可释放的 ApiFactory，而释放入口是 IAsyncLifetime.DisposeAsync——
// CA1001 只认 IDisposable 模式，识别不了 xUnit 的 IAsyncLifetime。
// 此处显式抑制，而不是为了让分析器满意去额外实现 IDisposable（那会让释放路径重复）。
#pragma warning disable CA1001
public class AuthEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _username = "admin";

    private ApiFactory _factory = null!;
    private Guid _userId;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();

        // 通过宿主的容器直接建用户（此时还没有任何创建用户的 API —— 那是 0.0.8）。
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync("admin");
        adminRole.Should().NotBeNull("内置角色应由迁移种入");

        var user = User.Create(Guid.NewGuid(), _username, hasher.Hash(_password), adminRole!.Id);
        await users.AddAsync(user);

        _userId = user.Id;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 登录成功_返回200与双令牌()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _username, password = _password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);
        body.Success.Should().BeTrue();
        body.Code.Should().Be(ErrorCodes.Success);

        var login = ReadData<LoginData>(body);
        login.AccessToken.Should().NotBeNullOrWhiteSpace();
        login.RefreshToken.Should().NotBeNullOrWhiteSpace();
        login.ExpiresIn.Should().Be(900);
        login.User.Id.Should().Be(_userId);
        login.User.Username.Should().Be(_username);
        login.User.Role.Should().Be("admin");
    }

    [Fact]
    public async Task 密码错误_返回401与统一ApiResult()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _username, password = "WrongP@ss123" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync(response);
        body.Success.Should().BeFalse();
        body.Code.Should().Be(ErrorCodes.Unauthenticated);
        body.Data.Should().BeNull();
    }

    [Fact]
    public async Task 用户不存在_与密码错误的响应完全一致()
    {
        using var client = _factory.CreateClient();

        using var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _username, password = "WrongP@ss123" });

        using var unknownUser = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "no-such-user", password = _password });

        // 【安全断言】任何差异（状态码、错误码、文案）都能被用来枚举用户名。
        unknownUser.StatusCode.Should().Be(wrongPassword.StatusCode);

        var unknownBody = await ReadAsync(unknownUser);
        var wrongBody = await ReadAsync(wrongPassword);

        unknownBody.Code.Should().Be(wrongBody.Code);
        unknownBody.Message.Should().Be(wrongBody.Message);
        unknownBody.Success.Should().Be(wrongBody.Success);
    }

    [Fact]
    public async Task 连续失败达上限_返回429与限流错误码()
    {
        using var client = _factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { username = _username, password = "WrongP@ss123" });

            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var blocked = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _username, password = _password });

        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var body = await ReadAsync(blocked);
        body.Code.Should().Be(ErrorCodes.LoginRateLimited);
    }

    [Theory]
    [InlineData("", AuthEndpointsTests._password)]
    [InlineData("   ", AuthEndpointsTests._password)]
    [InlineData(_username, "")]
    public async Task 用户名或密码为空_返回400与参数校验错误码(string username, string password)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await ReadAsync(response);
        body.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 当前用户_携带有效令牌_返回用户摘要()
    {
        using var client = CreateAuthorizedClient(CreateToken(_userId, DateTime.UtcNow));

        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);
        var user = ReadData<AuthenticatedUserData>(body);

        user.Id.Should().Be(_userId);
        user.Username.Should().Be(_username);
        user.Role.Should().Be("admin");
    }

    [Fact]
    public async Task 当前用户_未携带令牌_返回401与未认证错误码()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync(response);
        body.Success.Should().BeFalse();
        body.Code.Should().Be(ErrorCodes.Unauthenticated);
        body.Data.Should().BeNull();
    }

    [Fact]
    public async Task 当前用户_令牌已过期_返回401与令牌过期错误码()
    {
        // 关键区分：过期必须回 1101 而不是 1100，否则前端无法判断「该静默刷新」还是「该跳登录页」。
        var expired = CreateToken(_userId, DateTime.UtcNow.AddMinutes(-20));

        using var client = CreateAuthorizedClient(expired);
        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync(response);
        body.Code.Should().Be(ErrorCodes.TokenExpired);
    }

    [Fact]
    public async Task 当前用户_签名被篡改_返回401与未认证错误码()
    {
        var valid = CreateToken(_userId, DateTime.UtcNow);

        // 改签名段的**首个**字符。刻意不碰末字符：Base64url 末字符的低位属于被丢弃的填充位，
        // 改了也可能解出完全相同的签名字节，使这条测试变成假阳性。
        var segments = valid.Split('.');
        segments[2] = (segments[2][0] == 'A' ? 'B' : 'A') + segments[2][1..];
        var tampered = String.Join('.', segments);

        tampered.Should().NotBe(valid);

        using var client = CreateAuthorizedClient(tampered);
        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync(response);
        body.Code.Should().Be(ErrorCodes.Unauthenticated);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public async Task 当前用户_令牌格式非法_返回401与未认证错误码(string token)
    {
        using var client = CreateAuthorizedClient(token);
        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync(response);
        body.Code.Should().Be(ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task 当前用户_签名密钥不符_返回401与未认证错误码()
    {
        // 用另一个密钥签发（模拟密钥轮换前的旧令牌 / 伪造令牌）。
        var foreignKeyToken = CreateTokenWithSecret(
            "another-completely-different-signing-key-0123",
            _userId,
            DateTime.UtcNow);

        using var client = CreateAuthorizedClient(foreignKeyToken);
        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).Code.Should().Be(ErrorCodes.Unauthenticated);
    }

    /// <summary>
    /// 用宿主容器里的令牌服务签发一个令牌。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="issuedAt">签发时刻（传过去的时刻即可得到"已过期"的令牌）。</param>
    /// <returns>JWT 原文。</returns>
    private string CreateToken(Guid userId, DateTime issuedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();

        return tokens.CreateAccessToken(userId, _username, "admin", issuedAt).Value;
    }

    /// <summary>
    /// 用指定密钥直接签发一个令牌（绕过宿主的密钥配置）。
    /// </summary>
    private static string CreateTokenWithSecret(string secret, Guid userId, DateTime issuedAt)
    {
        var handler = new JsonWebTokenHandler();

        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "docker-manager",
            Audience = "docker-manager",
            IssuedAt = issuedAt,
            Expires = issuedAt.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtClaimNames.Subject] = userId.ToString("D"),
                [JwtClaimNames.UniqueName] = _username,
                [JwtClaimNames.Role] = "admin",
                [JwtClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
        });
    }

    private HttpClient CreateAuthorizedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static async Task<ApiResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResponse>())!;

    private static T ReadData<T>(ApiResponse body) =>
        body.Data!.Value.Deserialize<T>(JsonSerializerOptions.Web)!;

    private sealed record LoginData(string AccessToken, string RefreshToken, int ExpiresIn, UserData User);

    private sealed record UserData(Guid Id, string Username, string Role);

    private sealed record AuthenticatedUserData(Guid Id, string Username, string Role);
}
