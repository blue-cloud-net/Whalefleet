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
/// 主机管理端点（<c>/api/v1/admin/hosts/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么用真实宿主</b>：这组端点串起了「鉴权 → 参数校验 → 仓储 → 客户端失效」四层，
/// 用替身测只能证明每层各自能跑，证明不了它们接在一起是对的。宿主用真实的 SQLite
/// （启动时会种入 <c>local</c> 主机）与真实 JWT。</para>
/// <para><b>不断言连通性</b>：探活需要真实 Docker 守护进程，属于 <c>DockerFactAttribute</c>
/// 的管辖范围（见 0.0.8 的测试设施）。这里只断言「连不上时返回 <c>false</c> 而不是报错」。</para>
/// </remarks>
#pragma warning disable CA1001
public class AdminHostsEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";

    private ApiFactory _factory = null!;

    /// <summary>
    /// 反序列化选项：后端契约是 camelCase，而投影类型的属性是 PascalCase。
    /// 默认大小写敏感会让字段静默绑不上（例如 <c>accessToken</c> → <c>AccessToken</c> 得到 null），
    /// 表现为“登录成功却拿到空令牌”这种极难定位的现象。
    /// </summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();

        // 用户管理 API 属同一迭代但不是本组测试的对象，因此直接经容器建两个账号：
        // 一个 admin（可访问）与一个 operator（应被 403 拦下）。
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

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            passwordHash,
            adminRole);
        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _operatorName,
            passwordHash,
            operatorRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/admin/hosts/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 非管理员_返回403()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsync("/api/v1/admin/hosts/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 列表_包含启动时种入的本机主机()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var hosts = await ListAsync(client);

        hosts.Should().ContainSingle(host => host.Name == "local");
    }

    [Fact]
    public async Task 创建_成功后出现在列表里()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new { name = "remote-1", type = "tcp", endpoint = "tcp://127.0.0.1:2375" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReadDataAsync<HostPayload>(response);
        created.Name.Should().Be("remote-1");
        created.Enabled.Should().BeTrue();
        created.AuthKind.Should().Be("none");

        // 创建时不探活：Reachable 应为 null（"未探测"），而不是 false（"离线"）。
        created.Reachable.Should().BeNull();

        var hosts = await ListAsync(client);
        hosts.Should().Contain(host => host.Name == "remote-1");
    }

    [Fact]
    public async Task 创建_主机名重复返回1303()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new { name = "LOCAL", type = "socket", endpoint = "/var/run/docker.sock" });

        // 大小写不同也算重复（唯一索引是不区分大小写的）。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNameAlreadyExists);
    }

    [Theory]
    [InlineData("")]
    [InlineData("udp")]
    public async Task 创建_连接方式非法返回1000(string type)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new { name = "bad-type", type, endpoint = "tcp://127.0.0.1:2375" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_TLS凭据不完整返回1000且指明字段()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new
            {
                name = "partial-tls",
                type = "tcp",
                endpoint = "https://docker.example:2376",
                tlsConfig = new { caCertificatePath = "/certs/ca.pem" },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        (await ReadMessageAsync(response)).Should().Contain("clientCertificatePath");
    }

    [Fact]
    public async Task 创建_两种凭据同时给出返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new
            {
                name = "both-creds",
                type = "tcp",
                endpoint = "https://docker.example:2376",
                tlsConfig = new
                {
                    caCertificatePath = "/ca.pem",
                    clientCertificatePath = "/cert.pem",
                    clientKeyPath = "/key.pem",
                },
                basicAuth = new { username = "u", password = "p" },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_合法TLS配置只存路径且不回显私钥()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new
            {
                name = "tls-host",
                type = "tcp",
                endpoint = "https://docker.example:2376",
                tlsConfig = new
                {
                    caCertificatePath = "/certs/ca.pem",
                    clientCertificatePath = "/certs/cert.pem",
                    clientKeyPath = "/certs/key.pem",
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReadDataAsync<HostPayload>(response);
        created.AuthKind.Should().Be("tls");

        // 响应里只有认证方式，没有任何路径或凭据内容。
        (await ReadAsync(response)).GetRawText().Should().NotContain("/certs");
    }

    [Fact]
    public async Task 更新_改端点后生效且不改名()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        await CreateHostAsync(client, "updatable", "tcp://127.0.0.1:2375");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/updatable/update",
            new { endpoint = "tcp://127.0.0.1:2376", enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadDataAsync<HostPayload>(response);
        updated.Name.Should().Be("updatable", "主机名是对外标识，更新接口不接收 name");
        updated.Endpoint.Should().Be("tcp://127.0.0.1:2376");
        updated.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task 更新_主机不存在返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/nope/update",
            new { endpoint = "tcp://127.0.0.1:2375" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 更新_清空凭据与提供凭据互斥()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        await CreateHostAsync(client, "clear-creds", "tcp://127.0.0.1:2375");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/clear-creds/update",
            new
            {
                clearCredentials = true,
                basicAuth = new { username = "u", password = "p" },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 删除_之后不再出现在列表里()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        await CreateHostAsync(client, "deletable", "tcp://127.0.0.1:2375");

        using var response = await client.PostAsync("/api/v1/admin/hosts/deletable/delete", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var hosts = await ListAsync(client);
        hosts.Should().NotContain(host => host.Name == "deletable");

        // 删除是软删除：名字可以复用（部分唯一索引只约束未删除行）。
        await CreateHostAsync(client, "deletable", "tcp://127.0.0.1:2375");
    }

    [Fact]
    public async Task 测试未保存配置_连不上返回false而不是报错()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 1 号端口上不会有 Docker 守护进程；这里要的是"连不上"这一正常结果。
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/test",
            new { type = "tcp", endpoint = "tcp://127.0.0.1:1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadDataAsync<bool>(response)).Should().BeFalse();
    }

    [Fact]
    public async Task 测试未保存配置_字段非法仍返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/test",
            new { type = "socket", endpoint = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
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

    private static async Task CreateHostAsync(HttpClient client, string name, string endpoint)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/create",
            new { name, type = "tcp", endpoint });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "前置的创建步骤必须成功");
    }

    private static async Task<IReadOnlyList<HostPayload>> ListAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/api/v1/admin/hosts/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<List<HostPayload>>(response);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("message").GetString() ?? String.Empty;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>主机列表项的响应投影（只取断言用得到的字段）。</summary>
    private sealed record HostPayload(
        string Name,
        string Type,
        string Endpoint,
        bool Enabled,
        bool? Reachable,
        string AuthKind);

    private sealed record LoginPayload(string AccessToken);
}
