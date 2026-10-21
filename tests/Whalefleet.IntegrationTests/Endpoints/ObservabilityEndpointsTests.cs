using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// 磁盘用量（<c>/api/v1/hosts/{hostId}/system/df</c>）与容器健康批量查询
/// （<c>/api/v1/hosts/{hostId}/containers/health</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para>这两条端点都是「按需、昂贵」的查询：磁盘用量在镜像多的机器上要几秒，
/// 健康查询是 N 次 inspect。因此测试除了断言形状，还要证明三件事：</para>
/// <list type="number">
///   <item><description><b><c>type</c> 过滤真的只查那一类</b>（未查的类必须是 null，不是 0）；</description></item>
///   <item><description><b>非法 <c>type</c> 直接报 1000</b>，而不是「忽略后查全部」；</description></item>
///   <item><description><b>查不到的容器直接缺席</b>，不让整批失败。</description></item>
/// </list>
/// </remarks>
#pragma warning disable CA1001
public class ObservabilityEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

    /// <summary>容器启动命令：足够长，保证它在测试期间一直运行。</summary>
    private static readonly string[] _sleepCommand = ["sleep", "300"];

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

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_password),
            adminRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [DockerFact]
    public async Task 磁盘用量_四类全查时都给出桶()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/system/df",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var usage = await ReadDataAsync<DiskUsagePayload>(response);

        // 不带 type 时四类都查：每一类都必须给出桶（哪怕是空清单）
        usage.Images.Should().NotBeNull();
        usage.Containers.Should().NotBeNull();
        usage.Volumes.Should().NotBeNull();
        usage.BuildCache.Should().NotBeNull();

        usage.Images!.Items.Should().NotBeNull();
        usage.Images.TotalSize.Should().NotBeNull("本机守护进程支持 /system/df");
        usage.Images.TotalSize!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [DockerFact]
    public async Task 磁盘用量_type过滤只查一类其余为null()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/system/df?type=image",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var usage = await ReadDataAsync<DiskUsagePayload>(response);

        usage.Images.Should().NotBeNull();

        // 「没查」必须是 null（与「确实是 0」区分开）——
        // 给空桶会让人读成「这台机器上没有容器 / 卷 / 构建缓存」
        usage.Containers.Should().BeNull();
        usage.Volumes.Should().BeNull();
        usage.BuildCache.Should().BeNull();
    }

    [Fact]
    public async Task 磁盘用量_非法type返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/system/df?type=layer",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerImageFact]
    public async Task 健康查询_返回healthStatus与重启计数()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"obs-hc-{Guid.NewGuid():N}"[..16];
        var containerId = await CreateContainerAsync(client, name);

        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/health",
                new { containerIds = new[] { containerId } });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await ReadDataAsync<HealthResultPayload>(response);

            var item = result.Items.Should().ContainSingle().Subject;
            item.ContainerId.Should().Be(containerId);

            // 没配健康检查 → none（不是 null）：null 会与「查不到」混为一谈
            item.HealthStatus.Should().Be("none");
            item.RestartCount.Should().Be(0);
            item.Running.Should().BeFalse();
        }
        finally
        {
            await RemoveContainerAsync(client, containerId);
        }
    }

    [DockerFact]
    public async Task 健康查询_查不到的容器直接缺席()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/health",
            new { containerIds = new[] { "no-such-container" } });

        // 容器可能在两次刷新之间被删掉，那是列表页的正常竞态：
        // 单条失败不该让整批请求变成错误
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<HealthResultPayload>(response);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task 健康查询_空列表返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/health",
            new { containerIds = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    private static async Task<string> CreateContainerAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = DockerAvailability.ImageReference,
                name,
                cmd = _sleepCommand,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReadDataAsync<CreatedPayload>(response);

        return created.ContainerId;
    }

    private static async Task RemoveContainerAsync(HttpClient client, string containerId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/remove",
            new { containerId, force = true });

        // 清理失败不该掩盖真正的断言失败（容器可能已被别处删掉）
        _ = response;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

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

    private sealed record DiskUsagePayload(
        DiskUsageBucketPayload? Images,
        DiskUsageBucketPayload? Containers,
        DiskUsageBucketPayload? Volumes,
        DiskUsageBucketPayload? BuildCache);

    private sealed record DiskUsageBucketPayload(
        long? TotalCount,
        long? ActiveCount,
        long? TotalSize,
        long? Reclaimable,
        List<DiskUsageItemPayload> Items);

    private sealed record DiskUsageItemPayload(string Id, string? Name, long? Size, bool? InUse);

    private sealed record HealthResultPayload(List<HealthItemPayload> Items);

    private sealed record HealthItemPayload(
        string ContainerId,
        string Name,
        string HealthStatus,
        long RestartCount,
        long? ExitCode,
        bool OomKilled,
        bool Running);

    private sealed record CreatedPayload(string ContainerId);

    private sealed record LoginPayload(string AccessToken);
}
