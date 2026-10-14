using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerClientPool"/> 的行为测试：按连接复用、空闲回收、失效与释放。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：池存在的唯一理由是「切换主机不重建连接」。若只测工厂能构建客户端，
/// 这条契约（同端点共享实例）就没有任何测试守着，而它恰恰是最容易在重构中悄悄退化的一条。</para>
/// <para>测试不依赖真实 Docker 守护进程：构建客户端本身不建立连接；只有 Ping 需要守护进程，
/// 此处仅在断言「客户端已被释放」时用到（已释放的客户端 Ping 会立刻抛
/// <see cref="ObjectDisposedException"/>，无需守护进程）。</para>
/// </remarks>
public class DockerClientPoolTests
{
    private const string LocalSocket = "/var/run/docker.sock";
    private const string RemoteTcp = "tcp://127.0.0.1:2375";

    [Fact]
    public void GetOrCreate_ShouldReturnSameInstanceForRepeatedCalls()
    {
        using var pool = CreatePool();
        var connection = Connection("local", DockerHostType.Socket, LocalSocket);

        var first = pool.GetOrCreate(connection);
        var second = pool.GetOrCreate(connection);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void GetOrCreate_ShouldShareClientWhenDifferentHostsPointToSameDaemon()
    {
        // 这正是池存在的意义：两个主机名（写法不同）指向同一守护进程时，
        // 必须复用同一个客户端与 HTTP 连接池，切换主机才不会重建连接。
        using var pool = CreatePool();

        var direct = pool.GetOrCreate(Connection("local", DockerHostType.Socket, "/var/run/docker.sock"));
        var aliased = pool.GetOrCreate(Connection("local-alias", DockerHostType.Socket, "unix://var/run/docker.sock"));

        aliased.Should().BeSameAs(direct);
    }

    [Fact]
    public void GetOrCreate_ShouldReturnDistinctClientsForDistinctEndpoints()
    {
        using var pool = CreatePool();

        var local = pool.GetOrCreate(Connection("local", DockerHostType.Socket, LocalSocket));
        var remote = pool.GetOrCreate(Connection("remote", DockerHostType.Tcp, RemoteTcp));

        remote.Should().NotBeSameAs(local);
    }

    [Fact]
    public void GetOrCreate_ShouldThrowHostUnreachableForInvalidEndpoint()
    {
        using var pool = CreatePool();

        var act = () => pool.GetOrCreate(Connection("local", DockerHostType.Socket, "relative/docker.sock"));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Fact]
    public void GetOrCreate_ShouldRetryAfterBuildFailure()
    {
        // 首次取用时端点非法必然失败；随后换用合法端点，池必须能成功构建——
        // Lazy 会永久缓存异常，所以失败条目必须被移出池。
        using var pool = CreatePool();

        var failed = () => pool.GetOrCreate(Connection("local", DockerHostType.Socket, "relative/docker.sock"));
        failed.Should().Throw<BusinessException>();

        var client = pool.GetOrCreate(Connection("local", DockerHostType.Socket, LocalSocket));

        client.Should().NotBeNull();
    }

    [Fact]
    public async Task Invalidate_ShouldDisposeAndRebuildClient()
    {
        using var pool = CreatePool();
        var connection = Connection("local", DockerHostType.Socket, LocalSocket);

        var before = pool.GetOrCreate(connection);

        pool.Invalidate(connection);

        var after = pool.GetOrCreate(connection);

        after.Should().NotBeSameAs(before);

        var ping = async () => await before.System.PingAsync(CancellationToken.None);
        await FluentActions.Awaiting(ping).Should().ThrowAsync<ObjectDisposedException>("失效即释放旧客户端");
    }

    [Fact]
    public void Invalidate_ShouldBeIdempotentNoOpForUnknownConnection()
    {
        using var pool = CreatePool();

        var act = () => pool.Invalidate(Connection("missing", DockerHostType.Socket, LocalSocket));

        act.Should().NotThrow();
    }

    [Fact]
    public void Invalidate_ShouldBeNoOpForInvalidConnectionDescription()
    {
        // 连接描述非法时算不出池键，只能记日志后返回——不能因为「失效不了」就抛异常。
        using var pool = CreatePool();

        var act = () => pool.Invalidate(Connection("local", DockerHostType.Socket, "relative/docker.sock"));

        act.Should().NotThrow();
    }

    [Fact]
    public void GetOrCreate_ShouldRebuildClientWhenIdleTimeoutExceeded()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        using var pool = CreatePool(timeProvider, idleTimeout: TimeSpan.FromMinutes(30));
        var connection = Connection("local", DockerHostType.Socket, LocalSocket);

        var first = pool.GetOrCreate(connection);

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        var second = pool.GetOrCreate(connection);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void GetOrCreate_ShouldKeepClientWhileWithinIdleTimeout()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        using var pool = CreatePool(timeProvider, idleTimeout: TimeSpan.FromMinutes(30));
        var connection = Connection("local", DockerHostType.Socket, LocalSocket);

        var first = pool.GetOrCreate(connection);

        timeProvider.Advance(TimeSpan.FromMinutes(29));

        pool.GetOrCreate(connection).Should().BeSameAs(first);
    }

    [Fact]
    public void ReapIdleClients_ShouldRecycleOnlyIdleEntries()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        using var pool = CreatePool(timeProvider, idleTimeout: TimeSpan.FromMinutes(30));

        var localConnection = Connection("local", DockerHostType.Socket, LocalSocket);
        var remoteConnection = Connection("remote", DockerHostType.Tcp, RemoteTcp);

        var local = pool.GetOrCreate(localConnection);

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        var remote = pool.GetOrCreate(remoteConnection);

        var reaped = pool.ReapIdleClients();

        reaped.Should().Be(1, "只有 local 空闲超时");
        pool.GetOrCreate(remoteConnection).Should().BeSameAs(remote, "刚用过的连接不该被回收");
        pool.GetOrCreate(localConnection).Should().NotBeSameAs(local);
    }

    [Fact]
    public void ReapIdleClients_ShouldDoNothingWhenRecyclingDisabled()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        using var pool = CreatePool(timeProvider, idleTimeout: TimeSpan.Zero);
        var connection = Connection("local", DockerHostType.Socket, LocalSocket);

        var first = pool.GetOrCreate(connection);

        timeProvider.Advance(TimeSpan.FromDays(7));

        pool.ReapIdleClients().Should().Be(0);
        pool.GetOrCreate(connection).Should().BeSameAs(first, "禁用回收后客户端应长期驻留");
    }

    [Fact]
    public void GetOrCreate_ShouldThrowAfterDispose()
    {
        var pool = CreatePool();
        pool.GetOrCreate(Connection("local", DockerHostType.Socket, LocalSocket));

        pool.Dispose();

        var act = () => pool.GetOrCreate(Connection("local", DockerHostType.Socket, LocalSocket));

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_ShouldDisposePooledClients()
    {
        var pool = CreatePool();

        var client = pool.GetOrCreate(Connection("local", DockerHostType.Socket, LocalSocket));

        pool.Dispose();

        var ping = async () => await client.System.PingAsync(CancellationToken.None);
        await FluentActions.Awaiting(ping).Should().ThrowAsync<ObjectDisposedException>();
    }

    private static DockerClientPool CreatePool(TimeProvider? timeProvider = null, TimeSpan? idleTimeout = null) =>
        new(
            new DockerClientFactory(NullLogger<DockerClientFactory>.Instance),
            Options.Create(new DockerClientFactoryOptions
            {
                Pool = new DockerClientPoolOptions
                {
                    IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30),
                    ReapInterval = TimeSpan.FromMinutes(5),
                },
            }),
            timeProvider ?? TimeProvider.System,
            NullLogger<DockerClientPool>.Instance);

    private static DockerHostConnection Connection(string hostId, DockerHostType type, string endpoint) => new()
    {
        HostId = hostId,
        Type = type,
        Endpoint = endpoint,
    };
}
