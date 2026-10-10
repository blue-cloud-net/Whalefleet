using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerClientFactory"/> 的集成测试：客户端构建、按连接缓存、失效与空闲回收。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：工厂是懒加载的——若只做编译验证，底层 Docker SDK 的用法错误
/// 要等到 0.0.9 第一次真正调用时才暴露。本测试实际执行客户端构建，把「能编译」
/// 升级为「能构造出可用客户端」；同时锁定核心契约：<b>多主机切换复用连接</b>。</para>
/// <para>测试不依赖真实 Docker 守护进程：构建客户端本身不建立连接，
/// 只有 Ping 才需要守护进程（此处刻意指向不可达端点）。</para>
/// </remarks>
public class DockerClientFactoryTests
{
    private const string LocalSocket = "/var/run/docker.sock";
    private const string UnreachableTcp = "tcp://127.0.0.1:1";

    [Theory]
    [InlineData(DockerHostType.Tcp, "tcp://127.0.0.1:2375")]
    [InlineData(DockerHostType.Socket, "/var/run/docker.sock")]
    [InlineData(DockerHostType.Socket, "unix:///var/run/docker.sock")]
    public async Task GetClientAsync_ShouldBuildClientForSupportedEndpoints(DockerHostType type, string endpoint)
    {
        using var factory = CreateFactory(Host("local", type, endpoint));

        var client = await factory.GetClientAsync("local");

        client.Should().NotBeNull();
    }

    [Fact]
    public async Task GetClientAsync_ShouldThrowHostUnreachableForUnsupportedHostType()
    {
        // 启动校验会先拦住这种配置（见 DockerClientFactoryOptionsTests）；这里是纵深防御。
        using var factory = CreateFactory(Host("local", (DockerHostType)999, UnreachableTcp));

        var act = async () => await factory.GetClientAsync("local");

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Fact]
    public async Task GetClientAsync_ShouldReturnSameInstanceForRepeatedCalls()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var first = await factory.GetClientAsync("local");
        var second = await factory.GetClientAsync("local");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task GetClientAsync_ShouldShareClientWhenDifferentHostsPointToSameDaemon()
    {
        // 这正是本类存在的意义：两个主机名（写法不同）指向同一守护进程时，
        // 必须复用同一个客户端与 HTTP 连接池，切换主机才不会重建连接。
        using var factory = CreateFactory(
            Host("local", DockerHostType.Socket, "/var/run/docker.sock"),
            Host("local-alias", DockerHostType.Socket, "unix://var/run/docker.sock"));

        var direct = await factory.GetClientAsync("local");
        var aliased = await factory.GetClientAsync("local-alias");

        aliased.Should().BeSameAs(direct);
    }

    [Fact]
    public async Task GetClientAsync_ShouldReturnDistinctClientsForDistinctEndpoints()
    {
        using var factory = CreateFactory(
            Host("local", DockerHostType.Socket, LocalSocket),
            Host("remote", DockerHostType.Tcp, "tcp://127.0.0.1:2375"));

        var local = await factory.GetClientAsync("local");
        var remote = await factory.GetClientAsync("remote");

        remote.Should().NotBeSameAs(local);
    }

    [Fact]
    public async Task GetClientAsync_ShouldMatchHostIdIgnoringCase()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var client = await factory.GetClientAsync("LOCAL");

        client.Should().NotBeNull();
    }

    [Fact]
    public async Task GetClientAsync_ShouldThrowHostNotFoundForUnknownHost()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var act = async () => await factory.GetClientAsync("missing");

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task GetClientAsync_ShouldRetryAfterBuildFailure()
    {
        // 端点非法时首次取用必然失败；随后把配置改对，工厂必须能成功重建——
        // Lazy 会永久缓存异常，所以失败条目必须被移出缓存。
        var options = Options.Create(new DockerClientFactoryOptions
        {
            Hosts = [Host("local", DockerHostType.Socket, "relative/docker.sock")],
            ClientCache = DefaultCacheOptions(),
        });

        using var factory = CreateFactory(options, TimeProvider.System);

        var failed = async () => await factory.GetClientAsync("local");
        await failed.Should().ThrowAsync<BusinessException>();

        options.Value.Hosts[0].Endpoint = LocalSocket;

        var client = await factory.GetClientAsync("local");

        client.Should().NotBeNull();
    }

    [Fact]
    public async Task Invalidate_ShouldDisposeAndRebuildClient()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var before = await factory.GetClientAsync("local");

        factory.Invalidate("local");

        var after = await factory.GetClientAsync("local");

        after.Should().NotBeSameAs(before);

        var ping = async () => await before.System.PingAsync(CancellationToken.None);
        await ping.Should().ThrowAsync<ObjectDisposedException>("失效即释放旧客户端");
    }

    [Fact]
    public void Invalidate_ShouldBeIdempotentNoOpForUnknownHost()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var act = () => factory.Invalidate("missing");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetClientAsync_ShouldRebuildClientWhenIdleTimeoutExceeded()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);

        using var factory = CreateFactory(
            Options.Create(new DockerClientFactoryOptions
            {
                Hosts = [Host("local", DockerHostType.Socket, LocalSocket)],
                ClientCache = new DockerClientCacheOptions
                {
                    IdleTimeout = TimeSpan.FromMinutes(30),
                    ReapInterval = TimeSpan.FromMinutes(5),
                },
            }),
            timeProvider);

        var first = await factory.GetClientAsync("local");

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        var second = await factory.GetClientAsync("local");

        second.Should().NotBeSameAs(first);

        var ping = async () => await first.System.PingAsync(CancellationToken.None);
        await ping.Should().ThrowAsync<ObjectDisposedException>("空闲超时的客户端应被释放");
    }

    [Fact]
    public async Task GetClientAsync_ShouldKeepClientWhileWithinIdleTimeout()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);

        using var factory = CreateFactory(
            Options.Create(new DockerClientFactoryOptions
            {
                Hosts = [Host("local", DockerHostType.Socket, LocalSocket)],
                ClientCache = new DockerClientCacheOptions
                {
                    IdleTimeout = TimeSpan.FromMinutes(30),
                    ReapInterval = TimeSpan.FromMinutes(5),
                },
            }),
            timeProvider);

        var first = await factory.GetClientAsync("local");

        timeProvider.Advance(TimeSpan.FromMinutes(29));

        var second = await factory.GetClientAsync("local");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task ReapIdleClients_ShouldRecycleOnlyIdleEntries()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);

        using var factory = CreateFactory(
            Options.Create(new DockerClientFactoryOptions
            {
                Hosts =
                [
                    Host("local", DockerHostType.Socket, LocalSocket),
                    Host("remote", DockerHostType.Tcp, "tcp://127.0.0.1:2375"),
                ],
                ClientCache = new DockerClientCacheOptions
                {
                    IdleTimeout = TimeSpan.FromMinutes(30),
                    ReapInterval = TimeSpan.FromMinutes(5),
                },
            }),
            timeProvider);

        var local = await factory.GetClientAsync("local");

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        var remote = await factory.GetClientAsync("remote");

        var reaped = factory.ReapIdleClients();

        reaped.Should().Be(1, "只有 local 空闲超时");
        (await factory.GetClientAsync("remote")).Should().BeSameAs(remote, "刚用过的连接不该被回收");
        (await factory.GetClientAsync("local")).Should().NotBeSameAs(local);
    }

    [Fact]
    public async Task ReapIdleClients_ShouldDoNothingWhenRecyclingDisabled()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.UnixEpoch);

        using var factory = CreateFactory(
            Options.Create(new DockerClientFactoryOptions
            {
                Hosts = [Host("local", DockerHostType.Socket, LocalSocket)],
                ClientCache = new DockerClientCacheOptions
                {
                    IdleTimeout = TimeSpan.Zero,
                    ReapInterval = TimeSpan.FromMinutes(5),
                },
            }),
            timeProvider);

        var first = await factory.GetClientAsync("local");

        timeProvider.Advance(TimeSpan.FromDays(7));

        factory.ReapIdleClients().Should().Be(0);
        (await factory.GetClientAsync("local")).Should().BeSameAs(first, "禁用回收后客户端应长期驻留");
    }

    [Theory]
    [InlineData(DockerHostType.Tcp)]
    [InlineData(DockerHostType.Socket)]
    public async Task TestConnectionAsync_ShouldReturnFalseWhenDaemonUnreachable(DockerHostType type)
    {
        // 指向必然不存在的端点：期望优雅返回 false（而不是抛出或挂起）。
        var endpoint = type == DockerHostType.Tcp ? UnreachableTcp : "/nonexistent/docker.sock";

        using var factory = CreateFactory(Host("local", type, endpoint));

        var reachable = await factory.TestConnectionAsync(Connection("local", type, endpoint));

        reachable.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldReturnFalseForInvalidEndpoint()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var reachable = await factory.TestConnectionAsync(
            Connection("draft", DockerHostType.Socket, "relative/docker.sock"));

        reachable.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldNotTouchTheCache()
    {
        using var factory = CreateFactory(Host("local", DockerHostType.Tcp, UnreachableTcp));

        var cached = await factory.GetClientAsync("local");

        var reachable = await factory.TestConnectionAsync(Connection("local", DockerHostType.Tcp, UnreachableTcp));

        reachable.Should().BeFalse();
        (await factory.GetClientAsync("local")).Should().BeSameAs(cached, "连通性测试不得重建缓存中的客户端");
    }

    [Fact]
    public async Task GetClientAsync_ShouldThrowAfterDispose()
    {
        var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));
        await factory.GetClientAsync("local");

        factory.Dispose();

        var act = async () => await factory.GetClientAsync("local");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_ShouldDisposeCachedClients()
    {
        var factory = CreateFactory(Host("local", DockerHostType.Socket, LocalSocket));

        var client = await factory.GetClientAsync("local");

        factory.Dispose();

        var ping = async () => await client.System.PingAsync(CancellationToken.None);
        await ping.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static DockerHostOptions Host(string name, DockerHostType type, string endpoint) => new()
    {
        Name = name,
        Type = type,
        Endpoint = endpoint,
    };

    private static DockerHostConnection Connection(string hostId, DockerHostType type, string endpoint) => new()
    {
        HostId = hostId,
        Type = type,
        Endpoint = endpoint,
    };

    private static DockerClientCacheOptions DefaultCacheOptions() => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(30),
        ReapInterval = TimeSpan.FromMinutes(5),
    };

    private static DockerClientFactory CreateFactory(params DockerHostOptions[] hosts) =>
        CreateFactory(
            Options.Create(new DockerClientFactoryOptions
            {
                Hosts = [.. hosts],
                ClientCache = DefaultCacheOptions(),
            }),
            TimeProvider.System);

    private static DockerClientFactory CreateFactory(
        IOptions<DockerClientFactoryOptions> options,
        TimeProvider timeProvider) =>
        new(options, timeProvider, NullLogger<DockerClientFactory>.Instance);
}
