using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 私有 Registry 配置端点（<c>/api/v1/admin/registries/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>本组用例最关键的一条是「响应里绝不能出现口令」</b>：
/// 它不只断言「没有明文口令字段」，而是<b>把整个响应体当成字符串扫一遍</b>——
/// 因为把密文放进响应（字段名叫 <c>encryptedPassword</c> 之类）同样致命：
/// 抓到一次响应就等于拿到一把通用钥匙。</para>
/// <para><b>「不碰真 Registry」</b>：连通性测试会真的发 HTTP 请求，因此只测
/// 「不可达」与「参数非法」这两条不依赖外部服务的路径。</para>
/// </remarks>
#pragma warning disable CA1001
public class RegistryEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _noRoleName = "norole";

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

        adminRole.Should().NotBeNull("内置角色应由迁移种入");
        operatorRole.Should().NotBeNull("内置角色应由迁移种入");

        var passwordHash = hasher.Hash(_password);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, passwordHash, adminRole);
        await TestAccounts.CreateUserAsync(users, assignments, _operatorName, passwordHash, operatorRole);
        await TestAccounts.CreateUserOnlyAsync(users, _noRoleName, passwordHash);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ===== 鉴权 =====

    [Theory]
    [InlineData("list")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("test")]
    public async Task 未认证_返回401(string action)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"/api/v1/admin/registries/{action}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// operator 只带主机域权限，Registry 是<b>全局</b>权限——它不该看得见配置。
    /// </summary>
    [Fact]
    public async Task 无registry权限_返回403()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/registries/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 无任何角色_返回403()
    {
        using var client = await CreateAuthenticatedClientAsync(_noRoleName);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/registries/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ===== 列表 =====

    [Fact]
    public async Task 列表_无配置时返回空数组()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/registries/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadDataAsync<List<RegistryPayload>>(response);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task 列表_新增后可查到()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var created = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new
            {
                name = "内网 Harbor",
                serverAddress = "harbor.local:5000",
                username = "deploy-bot",
                password = "sup3r-s3cret",
            });

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/registries/list", new { });

        var items = await ReadDataAsync<List<RegistryPayload>>(response);

        items.Should().ContainSingle();
        items[0].Name.Should().Be("内网 Harbor");
        items[0].ServerAddress.Should().Be("harbor.local:5000");
        items[0].Username.Should().Be("deploy-bot");
        items[0].HasCredentials.Should().BeTrue();
    }

    [Fact]
    public async Task 列表_按关键字过滤()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");
        await AddRegistryAsync(client, "公网备份", "backup.example.com");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/list",
            new { search = "backup" });

        var items = await ReadDataAsync<List<RegistryPayload>>(response);

        items.Should().ContainSingle();
        items[0].Name.Should().Be("公网备份");
    }

    // ===== 新增 =====

    [Fact]
    public async Task 新增_成功且地址被规范化()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new
            {
                name = "  内网 Harbor  ",
                serverAddress = "  HTTPS://Harbor.Local:5000/  ",
                username = "deploy-bot",
                password = "sup3r-s3cret",
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReadDataAsync<RegistryPayload>(response);

        created.Name.Should().Be("内网 Harbor");
        created.ServerAddress.Should().Be("harbor.local:5000");
    }

    /// <summary>
    /// <b>本组最重要的断言</b>：响应体里不能出现口令的任何形式。
    /// </summary>
    [Fact]
    public async Task 新增_响应中不含口令明文也不含密文()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new
            {
                name = "内网 Harbor",
                serverAddress = "harbor.local:5000",
                username = "deploy-bot",
                password = "sup3r-s3cret",
            });

        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("sup3r-s3cret");
        body.Should().NotContain("encryptedPassword");
        body.Should().NotContain("encrypted_password");
    }

    [Fact]
    public async Task 新增_别名重复_返回1801()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name = "内网 Harbor", serverAddress = "other.local:5000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1801);
    }

    /// <summary>
    /// 同址重复必须单独报错——它会让「推镜像时用哪条凭据」变成随机行为。
    /// </summary>
    [Fact]
    public async Task 新增_地址重复_返回1802()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name = "另一个名字", serverAddress = "harbor.local:5000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1802);
    }

    [Fact]
    public async Task 新增_地址大小写不同仍算重复()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name = "另一个名字", serverAddress = "HARBOR.LOCAL:5000" });

        (await ReadCodeAsync(response)).Should().Be(1802);
    }

    [Theory]
    [InlineData(null, "harbor.local:5000")]
    [InlineData("", "harbor.local:5000")]
    [InlineData("名字", null)]
    [InlineData("名字", "")]
    [InlineData("名字", "不是合法地址")]
    public async Task 新增_参数非法_返回1000(string? name, string? serverAddress)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name, serverAddress });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1000);
    }

    /// <summary>
    /// 账号与口令必须成对：半截凭据推镜像时只会得到「认证失败」。
    /// </summary>
    [Fact]
    public async Task 新增_只有账号没有口令_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name = "内网 Harbor", serverAddress = "harbor.local:5000", username = "deploy-bot" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1000);
    }

    [Fact]
    public async Task 新增_只有口令没有账号_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name = "内网 Harbor", serverAddress = "harbor.local:5000", password = "sup3r-s3cret" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1000);
    }

    // ===== 删除 =====

    [Fact]
    public async Task 删除_成功后列表里没有()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var created = await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/remove",
            new { id = created.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var list = await client.PostAsJsonAsync("/api/v1/admin/registries/list", new { });

        (await ReadDataAsync<List<RegistryPayload>>(list)).Should().BeEmpty();
    }

    [Fact]
    public async Task 删除_不存在的配置_返回1800()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/remove",
            new { id = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(1800);
    }

    [Fact]
    public async Task 删除_重复调用_第二次返回1800()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var created = await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");

        using var first = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/remove",
            new { id = created.Id });

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/remove",
            new { id = created.Id });

        (await ReadCodeAsync(second)).Should().Be(1800);
    }

    /// <summary>
    /// 软删除后同名 / 同址可再次使用——唯一索引是部分索引（<c>WHERE is_deleted = 0</c>）。
    /// </summary>
    [Fact]
    public async Task 删除后_同名可再次创建()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var created = await AddRegistryAsync(client, "内网 Harbor", "harbor.local:5000");

        using var removed = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/remove",
            new { id = created.Id });

        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var recreated = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name = "内网 Harbor", serverAddress = "harbor.local:5000" });

        recreated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 连通性测试 =====

    [Fact]
    public async Task 测试_地址不可达_返回1805()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 用 RFC 5737 保留的文档地址段（TEST-NET-1）：保证不可路由，且不会误连到真实服务
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/test",
            new { serverAddress = "192.0.2.1:5000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadCodeAsync(response)).Should().Be(1805);
    }

    [Fact]
    public async Task 测试_指定不存在的配置_返回1800()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/test",
            new { id = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(1800);
    }

    [Fact]
    public async Task 测试_既没给主键也没给地址_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/test",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1000);
    }

    // ===== 辅助 =====

    private static async Task<RegistryPayload> AddRegistryAsync(HttpClient client, string name, string serverAddress)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/registries/add",
            new { name, serverAddress });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<RegistryPayload>(response);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken, string RefreshToken);

    private sealed record RegistryPayload(
        Guid Id,
        string Name,
        string ServerAddress,
        string Username,
        bool HasCredentials,
        string? Description);
}
