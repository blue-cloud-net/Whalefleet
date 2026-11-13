using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 容器重建 Webhook 端点（匿名触发 + 主机作用域管理）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>触发用例不携带 access token</b>：凭据就是令牌本身。
/// 若用例改用登录令牌来调，反而掩盖了「端点是不是真的匿名」这个问题。</para>
/// <para><b>「令牌无效一律 404」是要守住的契约</b>：不区分「不存在」「已吊销」，
/// 否则这个端点就成了令牌探测工具。</para>
/// <para><b>管理端点的令牌明文只回一次</b>：列表里只有提示（前几位），
/// 而明文只在轮换的响应里出现。</para>
/// <para><b>这里不真的重建容器</b>：重建需要真实镜像与启动等待，
/// 属于环境相关的验证（手工走查，见 roadmap 验收）。本组守住的是
/// 「令牌语义 / 鉴权 / 归属」这些只有端到端才暴露的契约。</para>
/// </remarks>
#pragma warning disable CA1001
public class ContainerWebhookEndpointsTests : IAsyncLifetime
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

    [Fact]
    public async Task 轮换令牌_返回明文且列表里只有提示()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var rotate = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/rotate",
            new { containerName = "web-1" });

        rotate.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(rotate)).GetProperty("data");
        var token = data.GetProperty("token").GetString();

        token.Should().NotBeNullOrWhiteSpace("轮换必须回带明文——否则用户拿不到令牌");

        using var list = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/list",
            new { });

        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(list);
        body.GetRawText().Should().NotContain(token!, "列表里只能有提示，不能有明文");
        body.GetProperty("data").EnumerateArray()
            .Should().Contain(item => item.GetProperty("containerName").GetString() == "web-1");
    }

    [Fact]
    public async Task 重复轮换同一容器_覆盖而不报错()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var rotate = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/webhook/rotate",
                new { containerName = "web-dupe" });

            rotate.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var list = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/list",
            new { });

        var items = (await ReadAsync(list)).GetProperty("data").EnumerateArray()
            .Where(item => item.GetProperty("containerName").GetString() == "web-dupe");

        items.Should().ContainSingle("同一容器只能有一条令牌");
    }

    [Fact]
    public async Task 吊销后触发_返回1911()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var rotate = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/rotate",
            new { containerName = "web-revoke" });

        var token = (await ReadAsync(rotate)).GetProperty("data").GetProperty("token").GetString()!;

        using var revoke = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/revoke",
            new { containerName = "web-revoke" });

        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        // 匿名触发：不带任何 access token。
        using var anonymous = _factory.CreateClient();
        using var trigger = await anonymous.PostAsJsonAsync(
            $"/api/v1/webhooks/containers/{token}",
            new { wait = false });

        trigger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(trigger)).GetProperty("code").GetInt32().Should().Be(1911);
    }

    [Fact]
    public async Task 令牌无效_返回1911而不是401()
    {
        using var anonymous = _factory.CreateClient();

        using var trigger = await anonymous.PostAsJsonAsync(
            "/api/v1/webhooks/containers/not-a-real-token",
            new { wait = false });

        // 404 而不是 401/403：这是匿名端点，未认证不是它的失败语义。
        trigger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(trigger)).GetProperty("code").GetInt32().Should().Be(1911);
    }

    [Fact]
    public async Task 吊销不存在的令牌_返回1915()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var revoke = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/revoke",
            new { containerName = "never-configured" });

        revoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(revoke)).GetProperty("code").GetInt32().Should().Be(1915);
    }

    [Fact]
    public async Task 未认证调用管理端点_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 容器名为空_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/rotate",
            new { containerName = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(response)).GetProperty("code").GetInt32().Should().Be(1000);
    }

    [Fact]
    public async Task 触发有效令牌_容器不存在时任务失败而非报错()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var rotate = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/webhook/rotate",
            new { containerName = "ghost-container" });

        var token = (await ReadAsync(rotate)).GetProperty("data").GetProperty("token").GetString()!;

        using var anonymous = _factory.CreateClient();
        using var trigger = await anonymous.PostAsJsonAsync(
            $"/api/v1/webhooks/containers/{token}",
            new { wait = false });

        // 受理成功（202）：容器是否存在是**执行期**才发现的，
        // 而失败会落在任务快照的 errorCode 上（1401），而不是 HTTP 状态码。
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var taskId = (await ReadAsync(trigger)).GetProperty("data").GetProperty("taskId").GetString()!;

        // 轮询到终态：容器不存在 → failed + 1401。
        var failed = await WaitForFailureAsync(anonymous, token, taskId);

        failed.Should().BeTrue("不存在的容器应在执行期失败，而不是永久停在 queued");
    }

    private async Task<bool> WaitForFailureAsync(HttpClient client, string token, string taskId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var response = await client.GetAsync($"/api/v1/webhooks/containers/{token}/tasks/{taskId}");

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var data = (await ReadAsync(response)).GetProperty("data");

            if (data.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            var status = data.GetProperty("status").GetString();

            if (String.Equals(status, "failed", StringComparison.Ordinal))
            {
                data.GetProperty("errorCode").GetInt32().Should().Be(1401);
                return true;
            }

            if (String.Equals(status, "succeeded", StringComparison.Ordinal))
            {
                return false;
            }

            await Task.Delay(250);
        }

        return false;
    }

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
