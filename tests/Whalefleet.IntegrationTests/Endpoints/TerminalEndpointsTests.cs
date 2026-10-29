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
/// 终端令牌端点（<c>POST /api/v1/hosts/{hostId}/containers/terminal-token</c>，0.8.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么这条端点的分支要被逐条钉住</b>：WebSocket 握手失败时浏览器只拿到一个
/// <c>close</c>（通常 1006），<b>看不到状态码、更看不到响应体</b>。因此「容器不存在 /
/// 未运行 / 没权限」这些诊断<b>只能</b>由本端点给出——它是用户唯一能看到原因的地方。</para>
/// <para><b>最关键的一条是「terminal token 不能当 access token」</b>：
/// 它靠独立 audience 实现，而不是一段"手工比对 typ 再拒绝"的代码。
/// 那条断言一旦失守，等于签发了一批 5 分钟有效、权限与用户完全相同的 access token。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class TerminalEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _viewerName = "viewer";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownContainer = "no-such-container-0123456789";

    /// <summary>让容器起得来但又不会自己退出。</summary>
    private static readonly string[] _keepAliveCommand = ["sleep", "120"];

    /// <summary>覆盖入口点：保证容器里一定跑着 <c>/bin/sh</c> 可用的环境。</summary>
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
        adminRole.Should().NotBeNull("内置角色应由迁移种入");

        var viewerRole = await roles.FindByNameAsync(_viewerName);
        viewerRole.Should().NotBeNull("内置角色应由迁移种入");

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_password),
            adminRole);

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _viewerName,
            hasher.Hash(_password),
            viewerRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证_返回1100()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/terminal-token",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task 主机不存在_返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/containers/terminal-token",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task viewer没有写权限_返回1502()
    {
        using var client = await CreateAuthenticatedClientAsync(_viewerName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/terminal-token",
            new { containerId = _unknownContainer });

        // 权限判定在碰守护进程**之前**：对没有写权限的人，
        // 连「这个容器到底在不在」都不该被告知。
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.OperationForbidden);
    }

    [Fact]
    public async Task Shell不在白名单_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/terminal-token",
            new { containerId = _unknownContainer, shell = "/usr/bin/fish" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerFact]
    public async Task 容器不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/terminal-token",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerShellImageFact]
    public async Task 容器未运行_返回1404()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = NewName();
        var containerId = await CreateContainerAsync(client, name, start: false);

        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/terminal-token",
                new { containerId });

            // exec 要求容器在跑；报 500 会让「先启动再试」变成「服务端坏了」。
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceConflict);
        }
        finally
        {
            await TryRemoveAsync(client, containerId);
        }
    }

    [DockerShellImageFact]
    public async Task 运行中的容器_签发令牌但该令牌不能访问普通接口()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = NewName();
        var containerId = await CreateContainerAsync(client, name, start: true);

        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/terminal-token",
                new { containerId });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var payload = await ReadDataAsync<TerminalTokenPayload>(response);

            payload.TerminalToken.Should().NotBeNullOrWhiteSpace();
            payload.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);

            // ⚠️ 核心安全断言：terminal token 与 access token 共用签名密钥，
            // 只有 audience 不同。若主鉴权放过了它，等于凭空多签发了一批
            // 有效期 5 分钟、权限完全相同的 access token。
            using var probe = _factory.CreateClient();
            probe.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", payload.TerminalToken);

            using var me = await probe.PostAsync("/api/v1/auth/me", content: null);

            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadCodeAsync(me)).Should().Be(ErrorCodes.Unauthenticated);
        }
        finally
        {
            await TryRemoveAsync(client, containerId);
        }
    }

    /// <summary>
    /// 生成一个唯一的容器名（避免并行测试撞名）。
    /// </summary>
    private static string NewName() => $"term-{Guid.NewGuid():N}"[..16];

    private static async Task<string> CreateContainerAsync(HttpClient client, string name, bool start)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = DockerAvailability.ShellImageReference,
                name,
                cmd = _keepAliveCommand,
                entrypoint = _shellEntrypoint,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReadDataAsync<CreatedPayload>(response);

        if (start)
        {
            using var started = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/start",
                new { containerId = created.ContainerId });

            started.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return created.ContainerId;
    }

    private static async Task TryRemoveAsync(HttpClient client, string containerId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/remove",
            new { containerId, force = true });

        // 清理失败不该掩盖真正的断言失败。
        _ = response;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue(
            "响应体：" + body.GetRawText());

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken);

    private sealed record CreatedPayload(string ContainerId);

    private sealed record TerminalTokenPayload(string TerminalToken, DateTimeOffset ExpiresAt);
}
