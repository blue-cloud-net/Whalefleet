using System.Threading.Channels;
using Whalefleet.Application.Abstractions.Realtime;

namespace Whalefleet.Application.Common;

/// <summary>
/// 进程内容器事件总线（基于 <see cref="Channel{T}"/>）。
/// </summary>
/// <remarks>
/// <para><b>为什么用 <see cref="Channel{T}"/> 而不是 <c>BlockingCollection</c></b>：
/// 通道的异步读写与取消语义更干净（<c>WaitToReadAsync</c> 直接支持取消令牌），
/// 且不需要在消费侧做轮询。</para>
/// <para><b>为什么满了丢最旧的</b>：发布方是 Docker 事件流的读取回调，
/// 阻塞它等于停掉事件流本身——那会让「事件没显示出来」变成「连守护进程都被拖住」。
/// 丢最旧的事件是可接受的：容器异常退出这类事件，几秒前的已经不值得推了
/// （用户真正需要的是「现在还活着吗」，那是 inspect 的职责）。</para>
/// <para><b>为什么每个订阅方有独立通道</b>：一个订阅方取消（页面卸载）不应影响其它订阅方。
/// 共享通道会让「谁先读谁拿到」，多订阅方时完全不可预测。</para>
/// </remarks>
public sealed class InProcessContainerEventBus : IContainerEventBus
{
    /// <summary>
    /// 单个订阅方的通道容量。
    /// </summary>
    /// <remarks>
    /// 512 条足够覆盖「短时间内几十个容器接连退出」的场景，
    /// 又不会让一个卡住的订阅方占住太多内存。
    /// </remarks>
    private const int SubscriberCapacity = 512;

    /// <summary>
    /// 初始化事件总线。
    /// </summary>
    public InProcessContainerEventBus()
    {
        _subscribers = [];
    }

    private readonly List<Channel<ContainerEventNotification>> _subscribers;

    /// <summary>
    /// 订阅方列表。
    /// </summary>
    /// <remarks>
    /// 用锁保护：发布与订阅可能来自不同线程（后台服务的定时器与 Hub 的连接生命周期）。
    /// 临界区极短（只做一次 Add / Remove），不构成争用热点。
    /// </remarks>
    private readonly object _gate = new();

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        ContainerEventNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        List<Channel<ContainerEventNotification>> targets;

        lock (_gate)
        {
            targets = [.. _subscribers];
        }

        foreach (var channel in targets)
        {
            // TryWrite 永远返回 true（满了会丢最旧的），因此这里不阻塞发布方。
            channel.Writer.TryWrite(notification);
        }

        await Task.CompletedTask.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ContainerEventNotification> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<ContainerEventNotification>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                // 满了丢最旧的：与 Publish 的策略一致，且不让慢订阅方拖住发布方。
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        }
    }
}
