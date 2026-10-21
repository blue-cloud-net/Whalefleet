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
        local.Daemon.Should().BeNull();

        // 标签体积很小（两个短字符串），列表里照给——界面要在行上显示「凭什么被筛出来」
        local.Tags.Should().NotBeNull();
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

        // 运行 + 停止 必然不超过总数（暂停中的容器两边都不算，因此可能严格小于）。
        overview.Counts.ContainersRunning.Should().BeGreaterThanOrEqualTo(0);
        overview.Counts.ContainersStopped.Should().BeGreaterThanOrEqualTo(0);
        (overview.Counts.ContainersRunning + overview.Counts.ContainersStopped)
            .Should().BeLessThanOrEqualTo(overview.Counts.Containers);

        // 健康 / 不健康只覆盖「配了健康检查」的容器，同样不可能超过总数。
        overview.Counts.ContainersHealthy.Should().BeGreaterThanOrEqualTo(0);
        overview.Counts.ContainersUnhealthy.Should().BeGreaterThanOrEqualTo(0);
        (overview.Counts.ContainersHealthy + overview.Counts.ContainersUnhealthy)
            .Should().BeLessThanOrEqualTo(overview.Counts.Containers);

        // 镜像磁盘占用来自 /system/df：本机守护进程支持，因此必须给得出数字
        // （给 null 会让界面显示「—」，而这个断言能拦住「调用写错了被静默吞掉」）。
        overview.Counts.ImagesSizeBytes.Should().NotBeNull();
        overview.Counts.ImagesSizeBytes!.Value.Should().BeGreaterThanOrEqualTo(0);

        // 0.6.0：守护进程的静态信息（全部来自同一次 /info，不新增调用）。
        // 本机是 Linux 上的真实守护进程，这几项必然给得出。
        overview.Daemon.Should().NotBeNull();
        overview.Daemon!.StorageDriver.Should().NotBeNullOrWhiteSpace("真实守护进程必然有存储驱动");
        overview.Daemon.LoggingDriver.Should().NotBeNullOrWhiteSpace();
        overview.Daemon.CpuCount.Should().BeGreaterThan(0);
        overview.Daemon.MemoryBytes.Should().BeGreaterThan(0);
        overview.Daemon.OperatingSystem.Should().NotBeNullOrWhiteSpace();
        overview.Daemon.KernelVersion.Should().NotBeNullOrWhiteSpace();

        // 新建的主机上没有任何标签：空数组而不是 null（null 会被读成「这趟没取」）
        overview.Tags.Should().NotBeNull();
        overview.Tags.Should().BeEmpty();
    }

    /// <summary>
    /// 概览响应**不得**再包含 <c>startedAt</c>。
    /// </summary>
    /// <remarks>
    /// 用原始 JSON 断言：反序列化会把「字段不存在」与「值为 null」抹平，
    /// 而这次变更的全部意义就是「把假字段彻底删掉」。
    /// </remarks>
    [Fact]
    public async Task 概览_不再返回主机运行时长()
    {
        var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/overview",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);
        var data = body.GetProperty("data");

        data.TryGetProperty("startedAt", out _).Should().BeFalse(
            "Docker Engine API 不提供守护进程启动时刻，0.6.0 已移除该字段");

        data.TryGetProperty("daemon", out var daemon).Should().BeTrue();
        daemon.ValueKind.Should().Be(JsonValueKind.Object);
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
        VersionPayload? Version,
        DaemonPayload? Daemon,
        List<TagPayload> Tags);

    private sealed record DaemonPayload(
        string? OperatingSystem,
        string? OsVersion,
        string? KernelVersion,
        string? StorageDriver,
        string? LoggingDriver,
        long CpuCount,
        long MemoryBytes);

    private sealed record TagPayload(Guid Id, string Name, string? Color);

    private sealed record CountsPayload(
        long Containers,
        long Images,
        long Volumes,
        long Networks,
        long ContainersRunning,
        long ContainersStopped,
        long ContainersHealthy,
        long ContainersUnhealthy,
        long? ImagesSizeBytes);

    private sealed record VersionPayload(string? Version, string? ApiVersion, string? Os, string? Arch);

    private sealed record LoginPayload(string AccessToken);
}
