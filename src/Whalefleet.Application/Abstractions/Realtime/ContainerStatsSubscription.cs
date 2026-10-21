using System.Threading.Channels;

namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 一次容器采样订阅：先给历史快照，再给实时流。
/// </summary>
/// <remarks>
/// <para><b>为什么历史与实时分成两段而不是一个流</b>：Hub 要把历史作为<b>一条</b>消息推给浏览器
/// （一次画出完整的 sparkline），而实时帧是逐条推的。合成一个流就得在帧上再打一个「是不是历史」
/// 的标记，反而多一层约定。</para>
/// <para><b>历史与实时不重叠</b>：快照与「挂上实时频道」在采样器的同一把锁内完成，
/// 因此不会出现同一条采样既在历史里又在实时流里，也不会刚好漏掉一条。</para>
/// </remarks>
public sealed class ContainerStatsSubscription : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private readonly ChannelReader<ContainerStatsSample> _reader;
    private int _disposed;

    /// <summary>
    /// 初始化订阅句柄。
    /// </summary>
    /// <param name="release">释放回调（由采样器提供，负责退订并在无人订阅时停泵）。</param>
    /// <param name="channel">本订阅专用的实时频道。</param>
    /// <param name="history">订阅时刻的环形缓冲快照（旧 → 新）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    internal ContainerStatsSubscription(
        Func<ValueTask> release,
        Channel<ContainerStatsSample> channel,
        IReadOnlyList<ContainerStatsSample> history)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(history);

        _release = release;
        _reader = channel.Reader;
        History = history;
    }

    /// <summary>
    /// 订阅时刻的历史采样（旧 → 新）；没有历史时为空数组。
    /// </summary>
    public IReadOnlyList<ContainerStatsSample> History { get; }

    /// <summary>
    /// 实时采样流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（客户端断开时由调用方取消）。</param>
    /// <returns>实时采样。</returns>
    public IAsyncEnumerable<ContainerStatsSample> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Hub 的泵任务与连接关闭可能同时走到这里；只允许释放一次
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _release().ConfigureAwait(false);
    }
}
