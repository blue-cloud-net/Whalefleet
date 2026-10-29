using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 交互式终端的 WebSocket 端点（<c>/ws/hosts/{hostId}/containers/exec</c>，0.8.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么用 <c>TestServer.CreateWebSocketClient</c> 而不是 SignalR 客户端</b>：
/// 这是<b>原生</b> WebSocket，不经过协商；<c>WebSocketClient</c> 直接走进程内管道，
/// 不占端口、不需要真实网络。</para>
/// <para><b>为什么失败路径只能断言「抛异常」</b>：握手被拒时服务端写的是普通 HTTP 响应
/// （带 <c>ApiResult</c> 与状态码），而 TestHost 的客户端只看「有没有升级成 101」——
/// 拿不到状态码与响应体。这与浏览器里的表现一致（前端同样只能看到连接失败），
/// 也正是「可诊断的错误必须由 REST 那一步给出」的原因。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class ContainerTerminalSocketTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _execPath = "/ws/hosts/local/containers/exec";

    /// <summary>容器里要跑的东西：保持存活即可（exec 会另起进程）。</summary>
    private static readonly string[] _keepAliveCommand = ["sleep", "120"];

    /// <summary>覆盖入口点，保证镜像里确实有 <c>/bin/sh</c>。</summary>
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
    public async Task 普通GET请求_返回1000而不是升级()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(_execPath);

        // 「这个端点只接受 WebSocket」是一个参数层面的错误（1000/400），
        // 而不是「未认证」——先判协议再判令牌，顺序不能反。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        body.GetProperty("code").GetInt32().Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 没有令牌_握手被拒()
    {
        var client = _factory.Server.CreateWebSocketClient();

        var act = () => client.ConnectAsync(new Uri($"ws://localhost{_execPath}"), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task 伪造令牌_握手被拒()
    {
        var client = _factory.Server.CreateWebSocketClient();

        var act = () => client.ConnectAsync(
            new Uri($"ws://localhost{_execPath}?token=not-a-real-token"),
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [DockerShellImageFact]
    public async Task 运行中的容器_能执行命令并读回输出()
    {
        using var http = await CreateAuthenticatedClientAsync();

        var containerId = await CreateAndStartContainerAsync(http);

        try
        {
            var token = await CreateTerminalTokenAsync(http, containerId);

            // 令牌与容器绑定：路径里不能换一个容器来复用同一张票。
            var socketPath = $"{_execPath}?token={Uri.EscapeDataString(token)}&cols=100&rows=30";

            var client = _factory.Server.CreateWebSocketClient();
            using var socket = await client.ConnectAsync(
                new Uri($"ws://localhost{socketPath}"),
                CancellationToken.None);

            // 用算术表达式而不是普通 echo：shell 会把**输入**回显一遍，
            // 若命令没真的执行，缓冲区里也能看到字面量 `echo 42`。
            // 只有真的执行了，才可能出现计算结果 `42`（输入里没有这个子串）。
            var payload = Encoding.UTF8.GetBytes("echo $((6*7))\n");
            await socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                CancellationToken.None);

            var output = await ReadUntilAsync(socket, "42", TimeSpan.FromSeconds(15));

            output.Should().Contain("42", "容器里的 shell 应当真的执行了这条命令");
        }
        finally
        {
            // socket 退出 using 作用域时服务端会收到断连并释放 exec 会话；
            // 容器本身由 force 删除兜底。
            await TryRemoveAsync(http, containerId);
        }
    }

    /// <summary>
    /// 读输出直到出现指定片段，或超时。
    /// </summary>
    /// <param name="socket">已建立的连接。</param>
    /// <param name="expected">期望出现的片段。</param>
    /// <param name="timeout">超时。</param>
    /// <returns>累计收到的文本。</returns>
    private static async Task<string> ReadUntilAsync(
        WebSocket socket,
        string expected,
        TimeSpan timeout)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();

        using var timeoutSource = new CancellationTokenSource(timeout);

        while (!timeoutSource.IsCancellationRequested)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (builder.ToString().Contains(expected, StringComparison.Ordinal))
            {
                break;
            }
        }

        return builder.ToString();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    private static async Task<string> CreateAndStartContainerAsync(HttpClient client)
    {
        var name = $"ws-{Guid.NewGuid():N}"[..16];

        using var created = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = DockerAvailability.ShellImageReference,
                name,
                cmd = _keepAliveCommand,
                entrypoint = _shellEntrypoint,
            });

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await ReadDataAsync<CreatedPayload>(created);

        using var started = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/start",
            new { containerId = payload.ContainerId });

        started.StatusCode.Should().Be(HttpStatusCode.OK);

        return payload.ContainerId;
    }

    private static async Task<string> CreateTerminalTokenAsync(HttpClient client, string containerId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/terminal-token",
            new { containerId, columns = 100, rows = 30 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await ReadDataAsync<TerminalTokenPayload>(response);

        return payload.TerminalToken;
    }

    private static async Task TryRemoveAsync(HttpClient client, string containerId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/remove",
            new { containerId, force = true });

        _ = response;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        body.GetProperty("success").GetBoolean().Should().BeTrue("响应体：" + text);

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private sealed record LoginPayload(string AccessToken);

    private sealed record CreatedPayload(string ContainerId);

    private sealed record TerminalTokenPayload(string TerminalToken, DateTimeOffset ExpiresAt);
}
