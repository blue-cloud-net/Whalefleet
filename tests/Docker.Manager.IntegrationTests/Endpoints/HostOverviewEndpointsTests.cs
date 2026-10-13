using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Users;
using Docker.Manager.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Endpoints;

/// <summary>
/// 主机概览端点（<c>/api/v1/hosts/{hostId}/overview</c>）的端到端测试。
/// </summary>
/// <remarks>
/// 这组用例补的是 0.0.8 的两个遗留项：主机版本信息（<c>GetVersionAsync</c>）与主机启动时间
/// （<c>GetSystemInfoAsync</c> 的 <c>SystemTime</c>）——它们此前只在 <c>docs/features.md</c>
/// 里被标着「属于 0.0.8」，代码里并没有对应端点。
/// </remarks>
#pragma warning disable CA1001
public class HostOverviewEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
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
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);
        var operatorRole = await roles.FindByNameAsync("operator");

        adminRole.Should().NotBeNull("内置角色应由迁移种入");
        operatorRole.Should().NotBeNull("内置角色应由迁移种入");

        var passwordHash = hasher.Hash(_password);

        await users.AddAsync(User.Create(Guid.NewGuid(), _adminName, passwordHash, adminRole!.Id));
        await users.AddAsync(User.Create(Guid.NewGuid(), _operatorName, passwordHash, operatorRole!.Id));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/overview", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 未知主机_返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync($"/api/v1/hosts/{_unknownHost}/overview", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 非管理员访问主机_返回1501()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/overview", content: null);

        // 与容器端点同一条可见性策略（fail-closed），因此这里同样先得到 403 而不是去连守护进程。
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    [DockerFact]
    public async Task 查询_返回版本与运行摘要()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/overview", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<OverviewPayload>(response);

        data.Version.Version.Should().NotBeNullOrWhiteSpace("真实守护进程必然有引擎版本");
        data.Version.ApiVersion.Should().NotBeNullOrWhiteSpace();
        data.Version.Os.Should().NotBeNullOrWhiteSpace();
        data.Version.Arch.Should().NotBeNullOrWhiteSpace();

        data.Runtime.Containers.Should().BeGreaterThanOrEqualTo(0);
        data.Runtime.ContainersRunning.Should().BeLessThanOrEqualTo(data.Runtime.Containers);
        data.Runtime.Cpus.Should().BeGreaterThan(0);
        data.Runtime.MemoryTotalBytes.Should().BeGreaterThan(0);

        // 启动时刻与「已运行秒数」要么都有、要么都没有（拿不到时刻时给 0 会被读成「刚刚启动」）。
        if (data.Runtime.StartedAt is not null)
        {
            data.Runtime.UptimeSeconds.Should().NotBeNull().And.BeGreaterThanOrEqualTo(0);
        }
        else
        {
            data.Runtime.UptimeSeconds.Should().BeNull();
        }
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

    /// <summary>主机概览响应的投影（只取断言用得到的字段）。</summary>
    private sealed record OverviewPayload(VersionPayload Version, RuntimePayload Runtime);

    private sealed record VersionPayload(string Version, string ApiVersion, string Os, string Arch);

    private sealed record RuntimePayload(
        long Containers,
        long ContainersRunning,
        long Cpus,
        long MemoryTotalBytes,
        DateTimeOffset? StartedAt,
        long? UptimeSeconds);

    private sealed record LoginPayload(string AccessToken);
}
