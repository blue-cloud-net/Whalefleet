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
/// 容器日志 Hub 的端到端测试（需要真实守护进程）。
/// </summary>
/// <remarks>
/// <para><b>为什么用 <c>since</c> 而不是默认的「只要新行」</b>：默认模式下
/// 实时流只推<b>订阅之后</b>产生的行，而测试容器在订阅之前就已经把启动日志写完了。
/// 传一个「容器启动之前」的 <c>since</c>，守护进程会把那之后的日志重放一遍——
/// 于是订阅一建立就能收到行，不必去猜「什么时候会有新输出」。</para>
/// <para><b>断言什么</b>：行确实被推到、序号单调递增、载荷形状与前端类型一致。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class ContainerLogHubTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

    /// <summary>等待日志到达的上限。</summary>
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    private static readonly string[] _keepAliveCommand = ["sleep", "30"];

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

    [DockerImageFact]
    public async Task 订阅容器日志_推来行且序号单调递增()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var image = DockerAvailability.ImageReference!;
        var name = $"dm-hub-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            var created = await PostDataAsync<CreatedPayload>(
                client,
                "create",
                new { image, name, cmd = _keepAliveCommand });

            containerId = created.ContainerId;

            await PostOkAsync(client, "start", new { containerId });
            await Task.Delay(TimeSpan.FromSeconds(2));

            var token = await LoginAsync();

            await using var connection = new HubConnectionBuilder()
                .WithUrl($"http://localhost/hubs/hosts/{_localHost}/container-log", options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();

            var received = new TaskCompletionSource<LogPayload>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            connection.On<LogPayload>("OnLog", value => received.TrySetResult(value));

            await connection.StartAsync();

            // 从「一分钟前」开始重放：容器启动时的日志就够我们验证推送了。
            var since = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

            var subscribed = await connection.InvokeAsync<ActionResult>(
                "SubscribeLogs",
                containerId,
                1,
                since);

            subscribed.Success.Should().BeTrue(subscribed.Message);

            var payload = await received.Task.WaitAsync(_timeout);

            payload.ContainerId.Should().Be(containerId);
            payload.Lines.Should().NotBeEmpty();

            // 序号单调递增（不要求连续——中间可能丢过行）。
            for (var i = 1; i < payload.Lines.Count; i++)
            {
                payload.Lines[i]!.Seq.Should().BeGreaterThan(payload.Lines[i - 1]!.Seq);
            }

            // 每行都有正文与时间戳。
            payload.Lines.Should().OnlyContain(item => item.Line != null);
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
    /// 日志推送的载荷。
    /// </summary>
    /// <param name="ContainerId">容器标识。</param>
    /// <param name="Lines">日志行。</param>
    private sealed record LogPayload(string ContainerId, IReadOnlyList<LogLine> Lines);

    /// <summary>
    /// 单条日志行。
    /// </summary>
    /// <param name="Seq">流内序号。</param>
    /// <param name="Timestamp">时间戳。</param>
    /// <param name="Line">正文。</param>
    private sealed record LogLine(int Seq, string? Timestamp, string Line);

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
