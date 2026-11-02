using System.Collections.Concurrent;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 跨请求共享的 Webhook 滑动窗口限流器，每个 Stack 每分钟最多受理十次。
/// </summary>
public sealed class StackWebhookRateLimiter(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, Queue<long>> _triggers = new();

    /// <summary>
    /// 原子地检查窗口并登记一次触发；被拒绝的请求不增加窗口记录。
    /// </summary>
    /// <param name="stackId">Stack 主键。</param>
    /// <returns>窗口内尚有名额时返回 true。</returns>
    public bool TryAcquire(Guid stackId)
    {
        var queue = _triggers.GetOrAdd(stackId, static _ => new Queue<long>());
        lock (queue)
        {
            var now = clock.GetTimestamp();
            while (queue.Count > 0 && clock.GetElapsedTime(queue.Peek(), now) >= TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= 10)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
