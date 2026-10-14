using Docker.DotNet;
using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Hosts;
using Whalefleet.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerClientPoolReaper"/> 的后台回收行为测试。
/// </summary>
/// <remarks>
/// <para>回收间隔本身就是 <see cref="TimeSpan"/>，因此测试可以把它压到毫秒级，
/// 不必真的等 5 分钟。</para>
/// <para><b>如何观察</b>：不轮询 <c>GetOrCreate</c>（那会刷新"最近使用时间"，导致永远不空闲），
/// 而是检测旧客户端是否已被释放——Ping 一个已释放的客户端会立刻抛
/// <see cref="ObjectDisposedException"/>，且不触碰池。</para>
/// </remarks>
public class DockerClientPoolReaperTests
{
    private static readonly DockerHostConnection _local = new()
    {
        HostId = "local",
        Type = DockerHostType.Socket,
        Endpoint = "/var/run/docker.sock",
    };

    [Fact]
    public async Task ExecuteAsync_ShouldRecycleIdleClients()
    {
        var poolOptions = new DockerClientPoolOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(50),
            ReapInterval = TimeSpan.FromMilliseconds(10),
        };

        using var pool = CreatePool(poolOptions);
        using var reaper = CreateReaper(pool, poolOptions);

        var client = pool.GetOrCreate(_local);

        await reaper.StartAsync(CancellationToken.None);

        try
        {
            (await WaitUntilDisposedAsync(client, TimeSpan.FromSeconds(10)))
                .Should().BeTrue("空闲客户端应被后台任务回收");

            pool.GetOrCreate(_local).Should().NotBeSameAs(client);
        }
        finally
        {
            await reaper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepClientsWhenRecyclingDisabled()
    {
        var poolOptions = new DockerClientPoolOptions
        {
            IdleTimeout = TimeSpan.Zero,
            ReapInterval = TimeSpan.FromMilliseconds(10),
        };

        using var pool = CreatePool(poolOptions);
        using var reaper = CreateReaper(pool, poolOptions);

        var client = pool.GetOrCreate(_local);

        await reaper.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(200);

            (await IsDisposedAsync(client)).Should().BeFalse("回收已禁用");
            pool.GetOrCreate(_local).Should().BeSameAs(client);
        }
        finally
        {
            await reaper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_ShouldStopTheRecycleLoop()
    {
        var poolOptions = new DockerClientPoolOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(50),
            ReapInterval = TimeSpan.FromMilliseconds(10),
        };

        using var pool = CreatePool(poolOptions);
        using var reaper = CreateReaper(pool, poolOptions);

        await reaper.StartAsync(CancellationToken.None);

        var act = async () => await reaper.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static async Task<bool> WaitUntilDisposedAsync(IDockerClient client, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsDisposedAsync(client))
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static async Task<bool> IsDisposedAsync(IDockerClient client)
    {
        try
        {
            await client.System.PingAsync(CancellationToken.None);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception)
        {
            // 客户端仍存活，只是守护进程不可达。
            return false;
        }
    }

    private static DockerClientPool CreatePool(DockerClientPoolOptions poolOptions) =>
        new(
            new DockerClientFactory(NullLogger<DockerClientFactory>.Instance),
            Options.Create(new DockerClientFactoryOptions { Pool = poolOptions }),
            TimeProvider.System,
            NullLogger<DockerClientPool>.Instance);

    private static DockerClientPoolReaper CreateReaper(
        DockerClientPool pool,
        DockerClientPoolOptions poolOptions) =>
        new(
            pool,
            Options.Create(new DockerClientFactoryOptions { Pool = poolOptions }),
            TimeProvider.System,
            NullLogger<DockerClientPoolReaper>.Instance);
}
