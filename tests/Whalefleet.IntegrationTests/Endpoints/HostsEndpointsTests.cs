using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 主机查询端点（<c>/api/v1/hosts/list</c> 与 <c>/api/v1/hosts/{hostId}/overview</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para>这两个端点喂的是前端总览页与主机选择器，因此除了「读得到数据」之外，还要证明两件容易被写错的事：
/// 列表对**非管理员**返回空数组（而不是 403），以及概览在主机离线时返回 <c>status=offline</c>
/// （而不是 502）——界面要把离线显示成一个状态，不是一个错误。</para>
/// <para>「主机离线」的分支不在本组用例里构造：本机守护进程可用，而关掉守护进程不是测试能做的事
/// （那属于 0.4.0 健康轮询的验证范围）。</para>
/// </remarks>
#pragma warning disable CA1001
public class HostsEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";

    /// <summary>没有任何角色分配的账号：用于验证 fail-closed。</summary>
    private const string _noRoleName = "norole";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";

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
        await TestAccounts.CreateUserOnlyAsync(users, _noRoleName, passwordHash);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 列表_未认证返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/hosts/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 列表_无任何角色分配的用户返回空数组而不是403()
    {
        using var client = await CreateAuthenticatedClientAsync(_noRoleName);

        using var response = await client.PostAsync("/api/v1/hosts/list", content: null);

        // 逐资源授权表落地前是 fail-closed：非管理员看不到任何主机。
        // 但列表用「空数组」表达这件事——返回 403 会让前端的主机选择器整块报错，
        // 而它本该显示「暂无可用主机」。
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var hosts = await ReadDataAsync<List<HostPayload>>(response);
        hosts.Should().BeEmpty();
    }

    [DockerFact]
    public async Task 列表_管理员可见本机且状态为在线()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync("/api/v1/hosts/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var hosts = await ReadDataAsync<List<HostPayload>>(response);
        var local = hosts.Should().ContainSingle(host => host.Id == _localHost).Subject;

        // Id 是主机名（前端路由与深链用的就是它），不是 hosts.id 那个 Guid。
        local.Name.Should().Be(_localHost);
        local.Enabled.Should().BeTrue();
        local.Status.Should().Be("online");

        // 列表刻意不取这几项：它们要额外调用守护进程，主机一多列表就会明显变慢。
        local.Counts.Should().BeNull();
        local.Version.Should().BeNull();
        local.StartedAt.Should().BeNull();
    }

    [Fact]
    public async Task 概览_未知主机返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync($"/api/v1/hosts/{_unknownHost}/overview", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 概览_无任何角色分配的用户返回1501()
    {
        using var client = await CreateAuthenticatedClientAsync(_noRoleName);

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/overview", content: null);

        // 单台概览走 CurrentHostMiddleware 的可见性校验，因此是 403 而不是空对象。
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    [DockerFact]
    public async Task 概览_返回版本与资源计数()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/overview", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var overview = await ReadDataAsync<HostPayload>(response);

        overview.Id.Should().Be(_localHost);
        overview.Status.Should().Be("online");

        overview.Version.Should().NotBeNull();
        overview.Version!.Version.Should().NotBeNullOrWhiteSpace("真实守护进程必然有引擎版本");
        overview.Version.ApiVersion.Should().NotBeNullOrWhiteSpace();
        overview.Version.Os.Should().NotBeNullOrWhiteSpace();
        overview.Version.Arch.Should().NotBeNullOrWhiteSpace();

        overview.Counts.Should().NotBeNull();
        overview.Counts!.Containers.Should().BeGreaterThanOrEqualTo(0);
        overview.Counts.Images.Should().BeGreaterThanOrEqualTo(0);

        // 卷 / 网络计数至少含 Docker 自建的 bridge 等，因此必然 > 0。
        overview.Counts.Volumes.Should().BeGreaterThan(0);
        overview.Counts.Networks.Should().BeGreaterThan(0);

        overview.StartedAt.Should().NotBeNull();
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
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>主机概览响应的投影（只取断言用得到的字段；对应前端的 <c>HostOverview</c>）。</summary>
    private sealed record HostPayload(
        string Id,
        string Name,
        string Type,
        string Endpoint,
        bool Enabled,
        string Status,
        CountsPayload? Counts,
        DateTimeOffset? StartedAt,
        VersionPayload? Version);

    private sealed record CountsPayload(long Containers, long Images, long Volumes, long Networks);

    private sealed record VersionPayload(string? Version, string? ApiVersion, string? Os, string? Arch);

    private sealed record LoginPayload(string AccessToken);
}
