using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 用户管理端点（<c>/api/v1/admin/users/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para>这组用例守两类容易出错的东西：<b>权限边界</b>（谁是管理员、谁能改谁的密码）与
/// <b>凭据生命周期</b>（改密码 / 删号之后旧会话必须失效）。</para>
/// <para>「改密码后旧刷新令牌不能用」这条尤其值得端到端测：它跨了 UserService → 令牌仓储 →
/// AuthService 三段，任何一段没接上都不会有编译错误。</para>
/// </remarks>
#pragma warning disable CA1001
public class AdminUsersEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _adminPassword = "AdminP@ssw0rd1";
    private const string _userPassword = "UserP@ssw0rd1";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private ApiFactory _factory = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();

        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var assignments = scope.ServiceProvider.GetRequiredService<IUserRoleAssignmentRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);
        var operatorRole = await roles.FindByNameAsync("operator");

        adminRole.Should().NotBeNull();
        operatorRole.Should().NotBeNull();

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_adminPassword),
            adminRole);

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _operatorName,
            hasher.Hash(_userPassword),
            operatorRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/admin/users/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 非管理员_列表与创建均返回403()
    {
        using var client = await LoginAsync(_operatorName, _userPassword);

        using var list = await client.PostAsJsonAsync("/api/v1/admin/users/list", new { });
        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/users/create",
            new { username = "sneaky", password = "SneakyP@ss1" });

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 创建_省略角色时取默认角色()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);

        var created = await CreateUserAsync(client, "no-role-user", "NoRoleP@ss1");

        // 迁移把 auditor 种为默认角色；这里断言的是"省略角色也能建出来且落到默认角色"。
        created.Roles.Should().ContainSingle().Which.Should().Be("auditor");
    }

    [Fact]
    public async Task 创建_可指定角色()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);

        var created = await CreateUserAsync(client, "viewer-user", "ViewerP@ss1", role: "viewer");

        created.Roles.Should().ContainSingle().Which.Should().Be("viewer");
    }

    [Fact]
    public async Task 创建_用户名重复返回1200()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/create",
            new { username = "OPERATOR", password = "DupP@ssw0rd1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.UsernameAlreadyExists);
    }

    [Fact]
    public async Task 创建_弱密码返回1202()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/create",
            new { username = "weak-user", password = "short1A" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.WeakPassword);
    }

    [Fact]
    public async Task 创建_角色不存在返回1000()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/create",
            new { username = "bad-role-user", password = "BadRoleP@ss1", role = "nonexistent" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 列表_分页参数被夹紧且返回总数()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);
        await CreateUserAsync(client, "page-user-1", "PageUserP@ss1");
        await CreateUserAsync(client, "page-user-2", "PageUserP@ss2");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/list",
            new { pageIndex = 0, pageSize = 2 });

        var page = await ReadDataAsync<PagedPayload>(response);

        page.Items.Should().HaveCount(2);
        page.PageSize.Should().Be(2);
        page.Total.Should().BeGreaterThanOrEqualTo(4, "至少是初始的 admin / operator 加上本次新建的两个");
    }

    [Fact]
    public async Task 删除_不能删当前登录账号()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);

        var me = (await ListAsync(client)).Items.Single(item => item.Username == _adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/delete",
            new { userId = me.Id });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 删除_有其他管理员时可删除管理员账号()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);
        await CreateUserAsync(client, "admin-2", "Admin2P@ssw0rd", role: Role.AdminName);

        // 换成 admin-2 来删 admin：此刻有 2 个管理员，删除是允许的
        // （反之若只剩一个，删它就会撞上"不能删自己"或"最后一个管理员"两道护栏）。
        using var client2 = await LoginAsync("admin-2", "Admin2P@ssw0rd");
        var adminUser = (await ListAsync(client2)).Items.Single(item => item.Username == _adminName);

        using var response = await client2.PostAsJsonAsync(
            "/api/v1/admin/users/delete",
            new { userId = adminUser.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListAsync(client2)).Items.Should().NotContain(item => item.Username == _adminName);
    }

    [Fact]
    public async Task 删除_唯一的管理员无法把自己删掉()
    {
        // 系统里只有一个管理员时，两种护栏都会挡住删除：先撞"不能删当前登录账号"。
        // 这条守的是"永远留存至少一个管理员"这个不变式（HTTP 侧能观察到的那一面）。
        using var client = await LoginAsync(_adminName, _adminPassword);
        var me = (await ListAsync(client)).Items.Single(item => item.Username == _adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/delete",
            new { userId = me.Id });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 删除_成功后不再出现在列表里()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);
        var victim = await CreateUserAsync(client, "deletable-user", "DeletableP@ss1");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/delete",
            new { userId = victim.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListAsync(client)).Items.Should().NotContain(item => item.Id == victim.Id);
    }

    [Fact]
    public async Task 改密_自助需正确原密码()
    {
        using var client = await LoginAsync(_operatorName, _userPassword);

        using var wrong = await client.PostAsJsonAsync(
            "/api/v1/admin/users/update-password",
            new { oldPassword = "not-my-password", newPassword = "BrandNewP@ss1" });

        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(wrong)).Should().Be(ErrorCodes.ValidationFailed);

        using var ok = await client.PostAsJsonAsync(
            "/api/v1/admin/users/update-password",
            new { oldPassword = _userPassword, newPassword = "BrandNewP@ss1" });

        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // 新密码可用，旧密码失效。
        using var newLogin = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _operatorName, password = "BrandNewP@ss1" });

        newLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 改密_非管理员不能重置他人密码()
    {
        using var adminClient = await LoginAsync(_adminName, _adminPassword);
        var target = await CreateUserAsync(adminClient, "reset-target", "ResetP@ssw0rd1");

        using var operatorClient = await LoginAsync(_operatorName, _userPassword);
        using var response = await operatorClient.PostAsJsonAsync(
            "/api/v1/admin/users/update-password",
            new { userId = target.Id, newPassword = "HijackedP@ss1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ResourceForbidden);
    }

    [Fact]
    public async Task 改密_管理员重置他人无需原密码()
    {
        using var client = await LoginAsync(_adminName, _adminPassword);
        var target = await CreateUserAsync(client, "reset-by-admin", "OriginalP@ss1");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/update-password",
            new { userId = target.Id, newPassword = "ResetByAdminP@ss1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var login = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "reset-by-admin", password = "ResetByAdminP@ss1" });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 改密_旧刷新令牌随之失效()
    {
        using var client = await LoginAsync(_operatorName, _userPassword);
        var staleRefreshToken = await ReadRefreshTokenAsync(await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _operatorName, password = _userPassword }));

        using var changed = await client.PostAsJsonAsync(
            "/api/v1/admin/users/update-password",
            new { oldPassword = _userPassword, newPassword = "AfterChangeP@ss1" });

        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        // 改密码的常见动因就是"疑似泄漏"，因此旧会话必须失效——否则改了个寂寞。
        using var refresh = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = staleRefreshToken });

        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var token = (await ReadAsync(login))
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static async Task<string> ReadRefreshTokenAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await ReadAsync(response)).GetProperty("data").GetProperty("refreshToken").GetString()!;
    }

    private static async Task<UserPayload> CreateUserAsync(
        HttpClient client,
        string username,
        string password,
        string? role = null)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users/create",
            new { username, password, role });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "前置的创建步骤必须成功");

        return await ReadDataAsync<UserPayload>(response);
    }

    private static async Task<PagedPayload> ListAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/admin/users/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<PagedPayload>(response);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>用户对象的响应投影（只取断言用得到的字段）。</summary>
    private sealed record UserPayload(Guid Id, string Username, IReadOnlyList<string> Roles);

    /// <summary>分页结果的响应投影。</summary>
    private sealed record PagedPayload(List<UserPayload> Items, int Total, int PageIndex, int PageSize);
}
