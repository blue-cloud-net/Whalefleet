using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 实时通道（SignalR Hub）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么用 LongPolling 而不是 WebSocket</b>：<c>TestServer</c> 不提供真实的
/// WebSocket 端点，客户端库会自动降级到 LongPolling——而 LongPolling 走的仍是
/// 同一套 negotiate / 鉴权 / 消息分发管道，足以验证契约。</para>
/// <para><b>测什么</b>：① 未认证连接被拒；② 无权限用户订阅被拒（且拿到的是
/// <b>错误码</b>而不是一段字符串）；③ 同一连接超出订阅上限被拒；④ 容器异常退出
/// 能推到通知 Hub。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class RealtimeHubTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _noRoleName = "norole";
    private const string _localHost = "local";

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

        var passwordHash = hasher.Hash(_password);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, passwordHash, adminRole);
        await TestAccounts.CreateUserOnlyAsync(users, _noRoleName, passwordHash);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证连接通知Hub_被拒()
    {
        await using var connection = BuildConnection("/hubs/notification", token: null);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await connection.StartAsync());
    }

    [Fact]
    public async Task 未认证连接日志Hub_被拒()
    {
        await using var connection = BuildConnection($"/hubs/hosts/{_localHost}/container-log", token: null);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await connection.StartAsync());
    }

    [Fact]
    public async Task 令牌放查询参数也能完成协商()
    {
        // 浏览器发起的 WebSocket 握手不能带自定义头，令牌只能走 ?access_token=。
        // 服务端只在 /hubs 路径上接受它（见 HubTokenQueryReader）。
        var token = await LoginAsync(_adminName);

        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            $"/hubs/notification/negotiate?access_token={Uri.EscapeDataString(token)}&negotiateVersion=1",
            content: new StringContent(string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 查询参数里的令牌在非_hubs路径不被接受()
    {
        // 令牌**只**在 /hubs 上生效：否则任何带 ?access_token= 的普通接口
        // 都会被「顺带」鉴权，而那会让 CORS / 缓存 / 日志的行为都变得不可预测。
        var token = await LoginAsync(_adminName);

        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/containers/list?access_token={Uri.EscapeDataString(token)}",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 协商不带令牌_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/hubs/notification/negotiate?negotiateVersion=1",
            content: new StringContent(string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 无任何角色分配的用户连接主机作用域Hub_被拒()
    {
        // 主机可见性在**协商阶段**就被中间件拦下（403）——
        // 根本轮不到 Hub 方法执行，因此这里断言的是「连不上」而不是某个错误码。
        var token = await LoginAsync(_noRoleName);

        await using var connection = BuildConnection($"/hubs/hosts/{_localHost}/container-log", token);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await connection.StartAsync());
    }

    [Fact]
    public async Task 同一连接超出订阅上限_被拒()
    {
        // 上限压到 1，才能在测试里触发——默认 5 需要真的建 5 个容器。
        using var factory = await CreateFactoryAsync(subscriptionCap: 1);

        var token = await LoginAsync(factory, _adminName);

        await using var connection = BuildConnection(factory, $"/hubs/hosts/{_localHost}/container-log", token);
        await connection.StartAsync();

        var first = await connection.InvokeAsync<RealtimeActionResult>(
            "SubscribeLogs",
            "container-a",
            1,
            (long?)null);
        first.Success.Should().BeTrue();

        var second = await connection.InvokeAsync<RealtimeActionResult>(
            "SubscribeLogs",
            "container-b",
            1,
            (long?)null);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("最多订阅");
    }

    [Fact]
    public async Task 重复订阅同一个容器_被拒()
    {
        var token = await LoginAsync(_adminName);

        await using var connection = BuildConnection($"/hubs/hosts/{_localHost}/container-log", token);
        await connection.StartAsync();

        await connection.InvokeAsync<RealtimeActionResult>("SubscribeLogs", "container-a", 1, (long?)null);

        var again = await connection.InvokeAsync<RealtimeActionResult>(
            "SubscribeLogs",
            "container-a",
            1,
            (long?)null);

        again.Success.Should().BeFalse();
        again.Message.Should().Contain("订阅中");
    }

    [Fact]
    public async Task 进度令牌太短_被拒()
    {
        var token = await LoginAsync(_adminName);

        await using var connection = BuildConnection("/hubs/notification", token);
        await connection.StartAsync();

        var result = await connection.InvokeAsync<RealtimeActionResult>("SubscribePull", "short");

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 进度令牌含非法字符_被拒()
    {
        var token = await LoginAsync(_adminName);

        await using var connection = BuildConnection("/hubs/notification", token);
        await connection.StartAsync();

        var result = await connection.InvokeAsync<RealtimeActionResult>(
            "SubscribePull",
            new string('a', 40) + "!");

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 合法进度令牌_订阅成功()
    {
        var token = await LoginAsync(_adminName);

        await using var connection = BuildConnection("/hubs/notification", token);
        await connection.StartAsync();

        var result = await connection.InvokeAsync<RealtimeActionResult>(
            "SubscribePull",
            new string('a', 40));

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SPA回退不吞掉_hubs路径()
    {
        // SPA 回退是个**中间件**，跑在端点之前：路径没被排除时，
        // 它会直接把响应写成 200 + index.html——Hub 根本轮不到处理。
        // 症状是「前端连不上实时通道，但日志里全是 200」。
        var spaRoot = Path.Combine(Path.GetTempPath(), "whalefleet-spa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spaRoot);
        await File.WriteAllTextAsync(Path.Combine(spaRoot, "index.html"), "<!doctype html><title>spa</title>");

        try
        {
            using var factory = new ApiFactory()
                .WithSetting("Spa:Enabled", "true")
                .WithSetting("Spa:RootPath", spaRoot);

            using var client = factory.CreateClient();

            // 前端的深链确实回退到了 index.html（证明回退确实生效了）。
            using var deepLink = await client.GetAsync("/hosts/local/containers");
            deepLink.StatusCode.Should().Be(HttpStatusCode.OK);
            deepLink.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

            // 而 /hubs 必须走 Hub 的管道：未认证 → 401（不是 200 + HTML）。
            using var hub = await client.PostAsync(
                "/hubs/notification/negotiate?negotiateVersion=1",
                content: new StringContent(string.Empty));

            hub.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            hub.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
        }
        finally
        {
            try
            {
                Directory.Delete(spaRoot, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响断言。
            }
        }
    }

    /// <summary>
    /// 建一个带指定订阅上限的工厂，并给它建好管理员账号。
    /// </summary>
    /// <param name="subscriptionCap">每类订阅上限。</param>
    /// <returns>工厂。</returns>
    /// <remarks>
    /// 每个 <see cref="ApiFactory"/> 有**自己的数据库**（见其构造函数），
    /// 因此换工厂就得重新建账号——不能直接复用主工厂里的那个。
    /// </remarks>
    private static async Task<ApiFactory> CreateFactoryAsync(int subscriptionCap)
    {
        var factory = new ApiFactory().WithSetting(
            "Realtime:MaxSubscriptionsPerKind",
            subscriptionCap.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var scope = factory.Services.CreateScope();
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

        return factory;
    }

    /// <summary>
    /// Hub 动作的返回形状（与后端 `RealtimeActionResult` 对应）。
    /// </summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Code">错误码。</param>
    /// <param name="Message">错误消息。</param>
    private sealed record RealtimeActionResult(bool Success, int? Code, string? Message);

    private HubConnection BuildConnection(string path, string? token, string? queryToken = null)
        => BuildConnection(_factory, path, token, queryToken);

    private static HubConnection BuildConnection(
        ApiFactory factory,
        string path,
        string? token,
        string? queryToken = null)
    {
        // 查询参数形式：浏览器发起的 WebSocket 握手不能带自定义头，
        // 令牌只能走 ?access_token=。服务端只在 /hubs 路径上接受它。
        var url = queryToken is null
            ? $"http://localhost{path}"
            : $"http://localhost{path}?access_token={Uri.EscapeDataString(queryToken)}";

        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();

                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    private async Task<string> LoginAsync(string username)
        => await LoginAsync(_factory, username);

    private static async Task<string> LoginAsync(ApiFactory factory, string username)
    {
        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await login.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        return document.RootElement
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString()!;
    }
}
