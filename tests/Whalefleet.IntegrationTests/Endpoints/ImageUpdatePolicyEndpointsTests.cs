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
/// 镜像自动更新策略端点的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么不打真仓库</b>：仓库探测走
/// <see cref="FakeRegistryManifestProbe"/> 注入（见 <c>ApiFactory.WithRegistryProbe</c>），
/// 因此这些用例验的是<b>平台自己的状态机</b>：基线建立、摘要变化、
/// 失败留档、重复策略拒绝——而不是「今天 Docker Hub 通不通」。</para>
/// <para><b>只测 notify 模式</b>：<c>auto</c> 模式会真的拉镜像并重建容器，
/// 那属于「跑一次真部署」的范畴（编排用例已经覆盖重建内核），
/// 在这里重测会让用例多出几十秒的拉取时间，却证明不了新东西。</para>
/// </remarks>
public class ImageUpdatePolicyEndpointsTests : IAsyncLifetime
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _reference = "ghcr.io/example/app:1.0";

    private static readonly string _digestA =
        "sha256:" + new string('a', 64);

    private static readonly string _digestB =
        "sha256:" + new string('b', 64);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly FakeRegistryManifestProbe _probe = new();

    private ApiFactory _factory = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory().WithRegistryProbe(_probe);

        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var assignments = scope.ServiceProvider.GetRequiredService<IUserRoleAssignmentRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);
        adminRole.Should().NotBeNull("内置角色应由迁移种入");

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_password),
            adminRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 新增策略_能列出来且初始状态为未知()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var policy = await CreatePolicyAsync(client, _reference);

        policy.GetProperty("imageReference").GetString().Should().Be(_reference);
        policy.GetProperty("mode").GetString().Should().Be("notify");
        policy.GetProperty("enabled").GetBoolean().Should().BeTrue();

        // 「还没查过」与「查过、确实最新」必须分开：合并会让刚加上的策略
        // 立刻宣称自己是最新的，而它一次都没查过。
        policy.GetProperty("state").GetString().Should().Be("unknown");
        policy.GetProperty("baselineDigest").ValueKind.Should().Be(JsonValueKind.Null);

        var list = await PostAsync(client, "list", new { });

        list.EnumerateArray()
            .Select(item => item.GetProperty("imageReference").GetString())
            .Should().Contain(_reference);
    }

    [Fact]
    public async Task 同一镜像重复新增_返回1406()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        await CreatePolicyAsync(client, _reference);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/update-policies/create",
            new { reference = _reference });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1406);
    }

    [Fact]
    public async Task 省略标签时补latest()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var policy = await CreatePolicyAsync(client, "ghcr.io/example/other");

        policy.GetProperty("imageReference").GetString().Should().Be("ghcr.io/example/other:latest");
    }

    [Fact]
    public async Task 首次检查只建立基线_不宣称有更新()
    {
        using var client = await this.CreateAuthenticatedClientAsync();
        _probe.WithDigest("ghcr.io/example/app", _digestA);

        var created = await CreatePolicyAsync(client, _reference);
        var id = created.GetProperty("id").GetString();

        var result = await PostAsync(client, "check", new { id });
        var policy = result.GetProperty("policy");

        policy.GetProperty("state").GetString().Should().Be("up-to-date");
        policy.GetProperty("baselineDigest").GetString().Should().Be(_digestA);
        result.GetProperty("recreatedContainers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task 远端摘要变化_报有更新且基线不前移()
    {
        using var client = await this.CreateAuthenticatedClientAsync();
        _probe.WithDigest("ghcr.io/example/app", _digestA);

        var created = await CreatePolicyAsync(client, _reference);
        var id = created.GetProperty("id").GetString();

        await PostAsync(client, "check", new { id });

        _probe.WithDigest("ghcr.io/example/app", _digestB);

        var policy = (await PostAsync(client, "check", new { id })).GetProperty("policy");

        policy.GetProperty("state").GetString().Should().Be("update-available");
        policy.GetProperty("remoteDigest").GetString().Should().Be(_digestB);
        policy.GetProperty("baselineDigest").GetString()
            .Should().Be(_digestA, "基线只在「应用成功」或「本地已跟上」时前进");
    }

    [Fact]
    public async Task 仓库不可达_返回200但状态为失败()
    {
        using var client = await this.CreateAuthenticatedClientAsync();
        _probe.WithFailure("ghcr.io/example/app", RegistryProbeStatus.Unreachable, "无法连接仓库");

        var created = await CreatePolicyAsync(client, _reference);
        var id = created.GetProperty("id").GetString();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/update-policies/check",
            new { id });

        // 仓库今天不通是「检查结论」，不是「请求有错」：
        // 译成 5xx 会让界面上分不清「我点错了」和「仓库不通」。
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var policy = (await ReadDataAsync(response)).GetProperty("policy");
        policy.GetProperty("state").GetString().Should().Be("failed");
        policy.GetProperty("message").GetString().Should().Be("无法连接仓库");
    }

    [Fact]
    public async Task 修改模式与启用状态()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var created = await CreatePolicyAsync(client, _reference);
        var id = created.GetProperty("id").GetString();

        var updated = await PostAsync(client, "update", new { id, mode = "notify", enabled = false });

        updated.GetProperty("enabled").GetBoolean().Should().BeFalse();
        updated.GetProperty("mode").GetString().Should().Be("notify");
    }

    [Fact]
    public async Task 模式非法_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var created = await CreatePolicyAsync(client, _reference);
        var id = created.GetProperty("id").GetString();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/update-policies/update",
            new { id, mode = "sometimes" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1000);
    }

    [Fact]
    public async Task 删除后不再出现在列表里()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var created = await CreatePolicyAsync(client, _reference);
        var id = created.GetProperty("id").GetString();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/update-policies/remove",
            new { id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await PostAsync(client, "list", new { });

        list.EnumerateArray()
            .Select(item => item.GetProperty("imageReference").GetString())
            .Should().NotContain(_reference);
    }

    [Fact]
    public async Task 删除不存在的策略_返回1405()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/update-policies/remove",
            new { id = Guid.NewGuid().ToString("D") });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(1405);
    }

    /// <summary>
    /// 新建一条策略并返回其投影。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="reference">镜像引用。</param>
    /// <returns>策略投影。</returns>
    private static Task<JsonElement> CreatePolicyAsync(HttpClient client, string reference) =>
        PostAsync(client, "create", new { reference, mode = "notify" });

    /// <summary>
    /// 调一个 RPC 动作并取 <c>data</c>。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="action">动作名。</param>
    /// <param name="body">请求体。</param>
    /// <returns><c>data</c> 节点。</returns>
    private static async Task<JsonElement> PostAsync(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/update-policies/{action}",
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "动作 {0} 应成功，实际 {1}：{2}",
            action,
            (int)response.StatusCode,
            await response.Content.ReadAsStringAsync());

        return await ReadDataAsync(response);
    }

    /// <summary>
    /// 读取响应体里的 <c>data</c>。
    /// </summary>
    /// <param name="response">响应。</param>
    /// <returns><c>data</c> 节点。</returns>
    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement.Clone();

        return json.GetProperty("data");
    }

    /// <summary>
    /// 读取响应体里的错误码。
    /// </summary>
    /// <param name="response">响应。</param>
    /// <returns>错误码。</returns>
    private static async Task<int> ReadCodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement.Clone();

        return json.GetProperty("code").GetInt32();
    }

    /// <summary>
    /// 以 admin 身份建一个已登录客户端。
    /// </summary>
    /// <returns>已认证客户端。</returns>
    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await login.Content.ReadAsStringAsync();
        var token = JsonDocument.Parse(text).RootElement.Clone()
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }
}
