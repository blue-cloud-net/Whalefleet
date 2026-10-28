using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Containers;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 容器资源采样器（<see cref="ContainerStatsSampler"/>）的单元测试。
/// </summary>
/// <remarks>
/// <para><b>为什么这组用例必须存在</b>：采样器是「订阅 → 起泵 → 节流 → 发布」的编排层，
/// 它的失败形态是<b>静默</b>的——泵一死，订阅者的频道就永远不结束，
/// 前端表现为「等待第一份采样…」转圈到天荒地老，而后端只有一条警告日志。
/// 真实事故就出在这里：节流用的初值是 <see cref="TimeSpan.MinValue"/>，
/// 而 <c>Elapsed - TimeSpan.MinValue</c> 会越过 <see cref="TimeSpan"/> 的表示范围，
/// <b>在第一帧上</b>抛 <see cref="OverflowException"/>——于是一个「看起来只是初值」的写法
/// 让整条指标链路从第一帧起就完全失效。</para>
/// <para>因此这里钉死三条契约：<b>首帧必达</b>、<b>历史可重放</b>、<b>节流只丢窗口内的帧</b>。</para>
/// </remarks>
public sealed class ContainerStatsSamplerTests
{
    /// <summary>等待采样到达的上限（假提供者一推就到，这个值只是防挂死的保险）。</summary>
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task 订阅后_第一帧采样能到达订阅者()
    {
        // 回归用例：首帧曾因节流初值（TimeSpan.MinValue）溢出而**整条流都推不出**。
        var provider = new FakeStatsProvider();
        var sampler = CreateSampler(provider);

        await using var subscription = sampler.Subscribe("local", "container-1");

        provider.Push(Sample(cpu: 12.5, memory: 1024));

        var received = await DrainAsync(subscription, _timeout, expectedCount: 1);

        received.Should().HaveCount(1, "首帧必须立即发布，不能被节流吃掉");
        received[0]!.CpuPercent.Should().Be(12.5);
        received[0]!.MemoryUsage.Should().Be(1024);
    }

    [Fact]
    public async Task 连续多帧_都会推给订阅者()
    {
        var provider = new FakeStatsProvider();
        var sampler = CreateSampler(provider, sampleIntervalMs: 0);

        await using var subscription = sampler.Subscribe("local", "container-1");

        provider.Push(Sample(cpu: 1));
        provider.Push(Sample(cpu: 2));
        provider.Push(Sample(cpu: 3));

        var received = await DrainAsync(subscription, _timeout, expectedCount: 3);

        received.Should().HaveCount(3);
        received.Select(item => item.CpuPercent).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public async Task 后来的订阅者_拿到历史快照()
    {
        var provider = new FakeStatsProvider();
        var sampler = CreateSampler(provider, sampleIntervalMs: 0);

        await using (var first = sampler.Subscribe("local", "container-1"))
        {
            provider.Push(Sample(cpu: 7));

            var received = await DrainAsync(first, _timeout, expectedCount: 1);

            received.Should().HaveCount(1);
        }

        await using var second = sampler.Subscribe("local", "container-1");

        second.History.Should().HaveCount(1, "历史缓冲要能重放，新订阅者不必从零开始攒");
        second.History[0]!.CpuPercent.Should().Be(7);
    }

    [Fact]
    public async Task 节流窗口内的帧被丢弃()
    {
        var provider = new FakeStatsProvider();
        var sampler = CreateSampler(provider, sampleIntervalMs: 1000);

        await using var subscription = sampler.Subscribe("local", "container-1");

        // 两帧连着推：第二帧落在首帧的节流窗口内，应当被丢掉。
        provider.Push(Sample(cpu: 1));
        provider.Push(Sample(cpu: 2));

        var received = await DrainAsync(subscription, TimeSpan.FromMilliseconds(500));

        received.Should().HaveCount(1);
        received[0]!.CpuPercent.Should().Be(1);
    }

    [Fact]
    public async Task 采样流出错时_订阅者读到异常()
    {
        // 失败必须**传出去**：不传的话订阅方会一直阻塞在「等下一帧」上，
        // 界面表现为「等待第一份采样…」转圈到天荒地老，而后端只有一条日志。
        var provider = new FakeStatsProvider { Failure = new InvalidOperationException("守护进程不可达") };
        var sampler = CreateSampler(provider);

        await using var subscription = sampler.Subscribe("local", "container-1");

        var act = async () =>
        {
            await foreach (var sample in subscription.ReadAllAsync())
            {
                _ = sample;
            }
        };

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("守护进程不可达");
    }

    [Fact]
    public async Task 退订后_采样流被停掉()
    {
        var provider = new FakeStatsProvider();
        var sampler = CreateSampler(provider, idleGraceSeconds: 0);

        var subscription = sampler.Subscribe("local", "container-1");

        await subscription.DisposeAsync();

        await provider.WaitCancelledAsync(_timeout);

        provider.Cancelled.Should().BeTrue("最后一个订阅者离开后应当停掉到 Docker 的那条流");
    }

    /// <summary>
    /// 构造采样器（假提供者 + 假作用域工厂）。
    /// </summary>
    /// <param name="provider">假采样提供者。</param>
    /// <param name="sampleIntervalMs">节流间隔（毫秒）。</param>
    /// <param name="idleGraceSeconds">空闲宽限期（秒）。</param>
    /// <returns>采样器。</returns>
    private static ContainerStatsSampler CreateSampler(
        FakeStatsProvider provider,
        int sampleIntervalMs = 1000,
        int idleGraceSeconds = 10)
    {
        var options = Options.Create(new RealtimeOptions
        {
            StatsSampleIntervalMs = sampleIntervalMs,
            StatsHistoryPoints = 60,
            StatsSamplerIdleGraceSeconds = idleGraceSeconds,
        });

        return new ContainerStatsSampler(
            new SingleProviderScopeFactory(provider),
            options,
            NullLogger<ContainerStatsSampler>.Instance);
    }

    /// <summary>
    /// 构造一条采样。
    /// </summary>
    /// <param name="cpu">CPU 百分比。</param>
    /// <param name="memory">内存占用。</param>
    /// <returns>采样。</returns>
    private static ContainerStatsSample Sample(double cpu, long memory = 0) =>
        new("container-1", cpu, memory, 0, 0, 0, null, null, DateTimeOffset.UtcNow);

    /// <summary>
    /// 在时间窗内读取订阅到的采样。
    /// </summary>
    /// <param name="subscription">订阅句柄。</param>
    /// <param name="window">时间窗。</param>
    /// <param name="expectedCount">读到这么多条就收工；为 <see langword="null"/> 时读满整个时间窗。</param>
    /// <returns>读到的采样。</returns>
    private static async Task<List<ContainerStatsSample>> DrainAsync(
        ContainerStatsSubscription subscription,
        TimeSpan window,
        int? expectedCount = null)
    {
        using var cancellation = new CancellationTokenSource(window);
        var received = new List<ContainerStatsSample>();

        try
        {
            await foreach (var sample in subscription.ReadAllAsync(cancellation.Token))
            {
                received.Add(sample);

                if (expectedCount is { } expected && received.Count >= expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 时间窗到了：这是读满即止的正常收尾。
        }

        return received;
    }

    /// <summary>
    /// 假的采样提供者：把推入的采样按需产出，流被取消时记一笔。
    /// </summary>
    private sealed class FakeStatsProvider : IContainerStatsProvider
    {
        private readonly Channel<ContainerStatsSample> _channel =
            Channel.CreateUnbounded<ContainerStatsSample>();

        /// <summary>流是否已被取消。</summary>
        public bool Cancelled { get; private set; }

        /// <summary>让流立即失败（为 <see langword="null"/> 时流正常产出采样）。</summary>
        public Exception? Failure { get; init; }

        /// <summary>
        /// 推入一条采样。
        /// </summary>
        /// <param name="sample">采样。</param>
        public void Push(ContainerStatsSample sample) => _channel.Writer.TryWrite(sample);

        /// <summary>
        /// 等待流被取消。
        /// </summary>
        /// <param name="timeout">上限。</param>
        /// <returns>表示异步操作的任务。</returns>
        public async Task WaitCancelledAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (!this.Cancelled && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<ContainerStatsSample> StreamStatsAsync(
            string hostId,
            string containerId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                if (Failure is not null)
                {
                    throw Failure;
                }

                await foreach (var sample in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return sample;
                }
            }
            finally
            {
                this.Cancelled = true;
            }
        }
    }

    /// <summary>
    /// 只提供一个服务的假作用域工厂（单测不引入 DI 实现包）。
    /// </summary>
    /// <param name="provider">假采样提供者。</param>
    private sealed class SingleProviderScopeFactory(IContainerStatsProvider provider) : IServiceScopeFactory
    {
        /// <inheritdoc />
        public IServiceScope CreateScope() => new SingleProviderScope(provider);
    }

    /// <summary>
    /// 只提供一个服务的假作用域。
    /// </summary>
    /// <param name="provider">假采样提供者。</param>
    private sealed class SingleProviderScope(IContainerStatsProvider provider) : IServiceScope
    {
        /// <inheritdoc />
        public IServiceProvider ServiceProvider { get; } = new SingleProvider(provider);

        /// <inheritdoc />
        public void Dispose()
        {
            // 无资源可释放。
        }
    }

    /// <summary>
    /// 只提供采样提供者的假服务提供者。
    /// </summary>
    /// <param name="provider">假采样提供者。</param>
    private sealed class SingleProvider(IContainerStatsProvider provider) : IServiceProvider
    {
        /// <inheritdoc />
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IContainerStatsProvider) ? provider : null;
    }
}
