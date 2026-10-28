using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 容器资源采样 Hub 的端到端测试（需要真实守护进程）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须有这组用例</b>：采样链路是「Hub → 采样器 → Docker 流」三段，
/// 任何一段断了，页面上都只表现为「等待第一份采样…」转圈——<b>没有任何错误提示</b>。
/// 真实事故就出在采样器的节流初值上：它在第一帧上抛 <c>OverflowException</c>，
/// 整条流从第一帧起就没了，而这个缺陷<b>只在真的连上守护进程时才会暴露</b>
/// （单测当时根本没覆盖采样器）。</para>
/// <para><b>测什么</b>：订阅成功后确实推来采样，且采样里的容器标识、内存占用都合理。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class ContainerStatsHubTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

    /// <summary>等待采样到达的上限。</summary>
    /// <remarks>
    /// Docker 的 stats 流本身是 1 秒一帧，订阅后还要经过采样器的节流窗口，
    /// 因此给到 30 秒——宁可等久一点，也不要让用例在慢机器上随机失败。
    /// </remarks>
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>让容器保持存活，采样流才有持续的数据。</summary>
    /// <remarks>
    /// ⚠️ **必须显式覆盖入口点**：镜像自带的 <c>ENTRYPOINT</c> 会抢在命令之前执行，
    /// 那样容器往往<b>秒退</b>——而容器一旦退出，Docker 给的采样就是<b>全零</b>
    /// （内存 0、网络 0），断言「内存占用 &gt; 0」会以「指标全缺」的面貌失败，
    /// 看起来像业务代码坏了，实际是容器根本没在跑。
    /// </remarks>
    private static readonly string[] _keepAliveEntrypoint = ["/bin/sh", "-c", "sleep 60"];

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

    [DockerShellImageFact]
    public async Task 订阅容器采样_推来采样且内容合理()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var image = DockerAvailability.ShellImageReference!;
        var name = $"ds-hub-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            var created = await PostDataAsync<CreatedPayload>(
                client,
                "create",
                new { image, name, entrypoint = _keepAliveEntrypoint });

            containerId = created.ContainerId;

            await PostOkAsync(client, "start", new { containerId });

            // 等容器真正跑起来：刚 start 完的那一瞬，采样可能还是「启动前」的空数据。
            await Task.Delay(TimeSpan.FromSeconds(2));

            var token = await LoginAsync();

            await using var connection = new HubConnectionBuilder()
                .WithUrl($"http://localhost/hubs/hosts/{_localHost}/container-stats", options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();

            var received = new TaskCompletionSource<StatsPayload>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            connection.On<StatsPayload>("OnStats", value => received.TrySetResult(value));

            await connection.StartAsync();

            var subscribed = await connection.InvokeAsync<ActionResult>(
                "SubscribeStats",
                containerId);

            subscribed.Success.Should().BeTrue(subscribed.Message);

            // 关键断言：订阅成功之后**必须**有采样推过来。
            // 采样器在第一帧上崩掉时，这里会一直等到超时——症状与线上完全一致。
            var payload = await received.Task.WaitAsync(_timeout);

            payload.ContainerId.Should().Be(containerId);
            payload.Stats.Should().NotBeNull();
            payload.Stats.ContainerId.Should().Be(containerId);
            payload.Stats.CpuPercent.Should().BeGreaterThanOrEqualTo(0);
            payload.Stats.MemoryUsage.Should().BeGreaterThan(0, "运行中的容器必然占用内存");
            payload.Stats.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        }
        finally
        {
            if (containerId is not null)
            {
                await TryRemoveAsync(client, containerId);
            }
        }
    }

    /// <summary>
    /// 采样推送的载荷。
    /// </summary>
    /// <param name="ContainerId">容器标识。</param>
    /// <param name="Stats">采样内容。</param>
    private sealed record StatsPayload(string ContainerId, StatsSample? Stats);

    /// <summary>
    /// 一条采样。
    /// </summary>
    /// <param name="ContainerId">容器标识。</param>
    /// <param name="CpuPercent">CPU 百分比。</param>
    /// <param name="MemoryUsage">内存占用。</param>
    /// <param name="MemoryLimit">内存上限。</param>
    /// <param name="NetworkRxBytes">累计接收字节。</param>
    /// <param name="NetworkTxBytes">累计发送字节。</param>
    /// <param name="Timestamp">采样时刻。</param>
    private sealed record StatsSample(
        string ContainerId,
        double CpuPercent,
        long MemoryUsage,
        long MemoryLimit,
        long NetworkRxBytes,
        long NetworkTxBytes,
        DateTimeOffset Timestamp);

    /// <summary>
    /// 订阅结果。
    /// </summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Code">错误码。</param>
    /// <param name="Message">错误消息。</param>
    private sealed record ActionResult(bool Success, int? Code, string? Message);

    /// <summary>
    /// 创建容器的响应。
    /// </summary>
    /// <param name="ContainerId">容器标识。</param>
    private sealed record CreatedPayload(string ContainerId);

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await LoginAsync());

        return client;
    }

    private async Task<string> LoginAsync()
    {
        using var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await login.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        return document.RootElement
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString()!;
    }

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);

        return document.RootElement.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task PostOkAsync(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task TryRemoveAsync(HttpClient client, string containerId)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/remove",
                new { containerId, force = true });

            _ = response;
        }
        catch (HttpRequestException)
        {
            // 清理失败不影响断言。
        }
    }
}
