using System.Threading.Channels;

using Whalefleet.Application.Abstractions;

namespace Whalefleet.Infrastructure.Sessions;

/// <summary>
/// 过期会话清理队列的 <see cref="Channel{T}"/> 实现（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>有界队列</b>：容量固定，满时<b>丢弃</b>入队请求而不是阻塞。
/// 丢弃是安全的——每小时还有一次全量兜底清理，漏掉几条会在那一轮被补上。
/// 反过来，阻塞会让「某个会话过期」这件事把请求线程卡住。</para>
/// </remarks>
internal sealed class SessionCleanupQueue : ISessionCleanupQueue
{
    /// <summary>
    /// 队列容量。
    /// </summary>
    private const int _capacity = 1024;

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(_capacity)
        {
            // 单读者：只有一个清理 Worker 消费。
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// 投递一个待清理的会话 id。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <returns>成功入队返回 <see langword="true"/>；队列已满返回 <see langword="false"/>。</returns>
    public bool TryEnqueue(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return false;
        }

        return _channel.Writer.TryWrite(sessionId);
    }

    /// <summary>
    /// 读取待清理的会话 id 流。
    /// </summary>
    /// <returns>会话 id 流。</returns>
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
