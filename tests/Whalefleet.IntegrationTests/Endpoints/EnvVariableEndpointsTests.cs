using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 环境变量端点（全局 <c>/api/v1/admin/variables/*</c> 与项目级
/// <c>/api/v1/hosts/{hostId}/composes/variables/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>本组最关键的一条是「响应里绝不能出现变量明文」</b>：
/// 它把整个响应体当成字符串扫一遍——因为把密文放进响应同样致命
/// （抓到一次响应就等于拿到一把通用钥匙）。</para>
/// <para><b>其次是「未定义变量在部署请求期就报错」</b>：
/// 它必须发生在 HTTP 响应里（而不是后台任务里），
/// 否则用户看到的是「部署失败」，而真正的原因（哪个变量没定义）被埋在日志里。</para>
/// </remarks>
#pragma warning disable CA1001
public class EnvVariableEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _localHost = "local";

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
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    #region 全局变量

    [Fact]
    public async Task 新建全局变量_返回掩码且响应里没有明文()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "DB_PASSWORD", value = "sup3r-s3cret", isSecret = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);
        var data = body.GetProperty("data");

        data.GetProperty("name").GetString().Should().Be("DB_PASSWORD");
        data.GetProperty("scope").GetString().Should().Be("global");
        data.GetProperty("isSecret").GetBoolean().Should().BeTrue();
        data.GetProperty("hasValue").GetBoolean().Should().BeTrue();

        // ⚠️ 整个响应体里都搜不到明文——包括密文字段。
        body.GetRawText().Should().NotContain("sup3r-s3cret");
    }

    [Fact]
    public async Task 列出全局变量_不含明文()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "PLAIN_VALUE", value = "plain-secret-value" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/variables/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);
        body.GetRawText().Should().NotContain("plain-secret-value");
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("name").GetString() == "PLAIN_VALUE");
    }

    [Fact]
    public async Task 更新全局变量_留空表示不修改()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "KEEP_ME", value = "original-value" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        // 只改备注，不带 value。
        using var update = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/update",
            new { name = "KEEP_ME", description = "只改备注" });

        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(update)).GetProperty("data");
        data.GetProperty("description").GetString().Should().Be("只改备注");
        data.GetProperty("hasValue").GetBoolean().Should().BeTrue("省略 value 不应清空已有值");
    }

    [Fact]
    public async Task 更新全局变量_清空值后hasValue为否()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "CLEAR_ME", value = "temporary" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using var update = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/update",
            new { name = "CLEAR_ME", clearValue = true });

        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(update)).GetProperty("data");
        data.GetProperty("hasValue").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task 删除全局变量_之后列表里不再出现()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "REMOVE_ME", value = "x" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using var remove = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/remove",
            new { name = "REMOVE_ME" });

        remove.StatusCode.Should().Be(HttpStatusCode.OK);

        using var list = await client.PostAsJsonAsync("/api/v1/admin/variables/list", new { });

        var body = await ReadAsync(list);
        body.GetProperty("data").EnumerateArray().Should().NotContain(item => item.GetProperty("name").GetString() == "REMOVE_ME");
    }

    [Fact]
    public async Task 变量名重复_返回1914()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var first = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "DUPLICATE", value = "a" });

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "DUPLICATE", value = "b" });

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(second)).GetProperty("code").GetInt32().Should().Be(1914);
    }

    [Fact]
    public async Task 变量名非法_返回1909()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "1BAD-NAME", value = "a" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(response)).GetProperty("code").GetInt32().Should().Be(1909);
    }

    [Fact]
    public async Task 更新不存在的变量_返回1913()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/update",
            new { name = "NOT_THERE", description = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(response)).GetProperty("code").GetInt32().Should().Be(1913);
    }

    [Fact]
    public async Task 未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/admin/variables/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 无variable权限_返回403()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/variables/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    #endregion

    #region 项目级变量与 Compose 注入

    [Fact]
    public async Task 项目级变量_与全局变量分开列出()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var global = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "SHARED", value = "global-value" });

        global.StatusCode.Should().Be(HttpStatusCode.OK);

        using var project = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/variables/create",
            new { composeName = "shop", name = "LOCAL_ONLY", value = "project-value" });

        project.StatusCode.Should().Be(HttpStatusCode.OK);

        using var list = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/variables/list",
            new { composeName = "shop" });

        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(list)).GetProperty("data");

        data.GetProperty("project").EnumerateArray()
            .Should().Contain(item => item.GetProperty("name").GetString() == "LOCAL_ONLY");
        data.GetProperty("inherited").EnumerateArray()
            .Should().Contain(item => item.GetProperty("name").GetString() == "SHARED");
    }

    [Fact]
    public async Task 部署_变量未定义_请求期返回1908()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/deploy",
            new
            {
                name = "undefdemo",
                composeContent = """
                    services:
                      web:
                        image: nginx:${NOT_DEFINED}
                    """,
                taskId = "task-undef-0000000000000000000001",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await ReadAsync(response);
        body.GetProperty("code").GetInt32().Should().Be(1908);
        body.GetProperty("message").GetString().Should().Contain("NOT_DEFINED");
    }

    [Fact]
    public async Task 部署_变量已定义_通过请求期校验()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "TAG", value = "1.2.3" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        // 未定义时是 1908；定义之后同一份 YAML 就能通过请求期校验（进后台任务）。
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/deploy",
            new
            {
                name = "defdemo",
                composeContent = """
                    services:
                      web:
                        image: nginx:${TAG}
                    """,
                taskId = "task-def-000000000000000000000001",
            });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task 详情_变量被删除后出现在未定义清单里()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        // 先声明变量再部署——部署必须通过请求期校验，项目的记录才会落库。
        using var create = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/create",
            new { name = "MISSING_ONE", value = "3.20" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using var deploy = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/deploy",
            new
            {
                name = "missingdemo",
                composeContent = """
                    services:
                      web:
                        image: alpine:${MISSING_ONE}
                    """,
                taskId = "task-miss-000000000000000000000001",
            });

        deploy.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 删掉变量：项目的 YAML 还在，但它引用的变量没了——
        // 这正是「未定义变量清单」要回答的场景（变量被误删 / 全局变量被改名）。
        using var remove = await client.PostAsJsonAsync(
            "/api/v1/admin/variables/remove",
            new { name = "MISSING_ONE" });

        remove.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/inspect",
            new { name = "missingdemo" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(response)).GetProperty("data");
        data.GetProperty("variables").GetProperty("undefined").EnumerateArray()
            .Should().Contain(item => item.GetString() == "MISSING_ONE");
    }
    #endregion

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(login)).GetProperty("data");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            data.GetProperty("accessToken").GetString());

        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
