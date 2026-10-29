using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 操作审计端点（0.9.0）：认证日志 / 活动日志 / 导出。
/// </summary>
/// <remarks>
/// <para><b>这几条用例守的是「审计真的写进去了」</b>：审计写入是 best-effort 的，
/// 失败只记日志不抛异常——因此「接口返回 200」完全不能证明记录存在。
/// 每条用例都是「做一次动作 → 再用查询端点把那条记录读回来」。</para>
/// <para><b>最关键的一条是「删了容器之后名字还在」</b>：如果实现只存 <c>target_id</c>，
/// 这条会红；而它的价值恰恰在于事后追责时能认出「是哪个容器」。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class AuditLogEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _viewerName = "viewer";
    private const string _localHost = "local";

    private static readonly string[] _keepAliveCommand = ["sleep", "120"];
    private static readonly string[] _shellEntrypoint = ["/bin/sh", "-c", "sleep 120"];

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
        var viewerRole = await roles.FindByNameAsync(_viewerName);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, hasher.Hash(_password), adminRole);
        await TestAccounts.CreateUserAsync(users, assignments, _viewerName, hasher.Hash(_password), viewerRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 登录失败会写进认证日志()
    {
        using var anonymous = _factory.CreateClient();

        using var failed = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "ghost", password = "Whatever1" });

        failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var admin = await CreateAuthenticatedClientAsync(_adminName);

        var result = await QueryAsync(admin, "authentication", new { actor = "ghost" });

        result.GetProperty("total").GetInt32().Should().BeGreaterThan(0);

        var first = result.GetProperty("items")[0];

        first.GetProperty("action").GetString().Should().Be("auth.login");
        first.GetProperty("outcome").GetString().Should().Be("failure");
        first.GetProperty("errorCode").GetInt32().Should().Be(ErrorCodes.Unauthenticated);
        first.GetProperty("actorName").GetString().Should().Be("ghost");
    }

    [Fact]
    public async Task 没有audit_read的角色返回1502()
    {
        using var viewer = await CreateAuthenticatedClientAsync(_viewerName);

        using var response = await viewer.PostAsJsonAsync(
            "/api/v1/admin/logs/activity/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 策略级拒权由 `OnForbidden` 统一写成 1500（与其它策略端点一致），
        // 1502 留给「应用服务在用例内部判定的缺权限」。
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ResourceForbidden);
    }

    [DockerShellImageFact]
    public async Task 删除容器后活动日志仍显示原容器名()
    {
        using var admin = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"audit-{Guid.NewGuid():N}"[..18];

        using var created = await admin.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = DockerAvailability.ShellImageReference,
                name,
                cmd = _keepAliveCommand,
                entrypoint = _shellEntrypoint,
            });

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var createdBody = await ReadAsync(created);
        var containerId = createdBody.GetProperty("data").GetProperty("containerId").GetString();

        // 先启动（产生一条 container.start），再删除（产生 container.remove + 名字快照）
        using var started = await admin.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/start",
            new { containerId });
        started.StatusCode.Should().Be(HttpStatusCode.OK);

        using var removed = await admin.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/remove",
            new { containerId, force = true });
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await QueryAsync(admin, "activity", new { search = name, targetType = "container" });

        var items = result.GetProperty("items").EnumerateArray().ToArray();

        items.Should().NotBeEmpty("启动与删除都应留下记录");

        // 容器此刻已经不存在了，但记录里的名字仍是删除时的名字。
        items.Should().Contain(entry => entry.GetProperty("targetName").GetString() == name);
        items.Should().Contain(entry => entry.GetProperty("action").GetString() == "container.remove");
    }

    [Fact]
    public async Task 导出CSV_带BOM且表头与页面列一致()
    {
        using var admin = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await admin.PostAsJsonAsync(
            "/api/v1/admin/logs/activity/export",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");

        var bytes = await response.Content.ReadAsByteArrayAsync();

        // BOM：Excel 靠它判定 UTF-8，缺了中文列头会乱码。
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);

        var text = Encoding.UTF8.GetString(bytes);

        text.Should().Contain("操作人");
        text.Should().Contain("动作");
        text.Should().Contain("目标");
        text.Should().Contain("主机");
        text.Should().Contain("结果");
    }

    /// <summary>
    /// 按类别查询审计日志。
    /// </summary>
    private static async Task<JsonElement> QueryAsync(HttpClient client, string category, object query)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/logs/{category}/list",
            query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue("响应体：" + body.GetRawText());

        return body.GetProperty("data");
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(login);
        var token = body.GetProperty("data").GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
