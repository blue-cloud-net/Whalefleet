using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 容器异常退出通知的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么直接向事件总线发布</b>：事件的<b>来源</b>是 Docker 的 events 流
/// （<c>ContainerEventWatcher</c>），那需要真的容器被杀掉，时序不可控。
/// 这里要验的是「事件 → 通知 Hub」这一段，因此直接向
/// <see cref="IContainerEventBus"/> 发布，把来源那一段隔离开。</para>
/// <para><b>为什么反复发布</b>：推送模型下「客户端还没连上时发布的事件」是
/// <b>收不到</b>的（这是设计如此：事件的价值在于及时）。而客户端库的
/// <c>StartAsync</c> 返回时，服务端侧的连接<b>可能还没登记</b>（长轮询要到下一次
/// 轮询才真正建立），于是「连上就立刻发一条」存在竞态。生产上不存在这个问题
/// （通知 Hub 在应用加载时就连上了），测试里反复发几次等的是「连接真正就绪」那个
/// 时刻。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class RealtimeNotificationTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

    /// <summary>等待通知到达的上限。</summary>
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    /// <summary>重复发布的间隔。</summary>
    private static readonly TimeSpan _retryInterval = TimeSpan.FromMilliseconds(200);

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
    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task 容器异常退出_推到通知Hub()
    {
        var received = await ConnectAndReceiveAsync();

        var result = await PublishUntilReceivedAsync(
            new ContainerEventNotification(
                _localHost,
                "container-abc",
                "web",
                137,
                true,
                DateTimeOffset.UtcNow),
            received);

        result.ContainerId.Should().Be("container-abc");
        result.ContainerName.Should().Be("web");
        result.OutOfMemory.Should().BeTrue();
        result.ExitCode.Should().Be(137);
    }

    [Fact]
    public async Task 容器正常退出_也推送但退出码为0()
    {
        var received = await ConnectAndReceiveAsync();

        var result = await PublishUntilReceivedAsync(
            new ContainerEventNotification(
                _localHost,
                "container-normal",
                "worker",
                0,
                false,
                DateTimeOffset.UtcNow),
            received);

        result.ExitCode.Should().Be(0);
        result.OutOfMemory.Should().BeFalse();
    }

    /// <summary>
    /// 连上通知 Hub 并登记好接收回调。
    /// </summary>
    /// <returns>接收任务。</returns>
    private async Task<TaskCompletionSource<ContainerDiedEvent>> ConnectAndReceiveAsync()
    {
        var token = await LoginAsync(_adminName);

        var connection = BuildConnection(token);

        var received = new TaskCompletionSource<ContainerDiedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<ContainerDiedEvent>("OnContainerDied", value => received.TrySetResult(value));

        await connection.StartAsync();

        // 连接要活到断言结束：把它挂到测试实例的字段上，由 DisposeAsync 释放。
        _connection = connection;

        return received;
    }

    /// <summary>测试期间保持打开的连接。</summary>
    private HubConnection? _connection;

    /// <summary>
    /// 反复发布事件，直到客户端收到。
    /// </summary>
    /// <param name="notification">事件内容。</param>
    /// <param name="received">接收任务。</param>
    /// <returns>客户端收到的事件。</returns>
    private async Task<ContainerDiedEvent> PublishUntilReceivedAsync(
        ContainerEventNotification notification,
        TaskCompletionSource<ContainerDiedEvent> received)
    {
        using var scope = _factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IContainerEventBus>();

        var deadline = DateTime.UtcNow + _timeout;

        while (DateTime.UtcNow < deadline && !received.Task.IsCompleted)
        {
            await bus.PublishAsync(notification);
            await Task.Delay(_retryInterval);
        }

        return await received.Task.WaitAsync(_timeout);
    }

    private HubConnection BuildConnection(string token)
    {
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notification", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    private async Task<string> LoginAsync(string username)
    {
        using var client = _factory.CreateClient();

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

    /// <summary>
    /// 通知事件在 Hub 侧的载荷。
    /// </summary>
    /// <param name="HostId">主机标识。</param>
    /// <param name="ContainerId">容器标识。</param>
    /// <param name="ContainerName">容器名。</param>
    /// <param name="ExitCode">退出码。</param>
    /// <param name="OutOfMemory">是否 OOM。</param>
    /// <param name="OccurredAt">发生时刻。</param>
    private sealed record ContainerDiedEvent(
        string HostId,
        string ContainerId,
        string ContainerName,
        long ExitCode,
        bool OutOfMemory,
        DateTimeOffset OccurredAt);
}
