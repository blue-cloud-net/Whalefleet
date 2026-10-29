using System.Collections.Concurrent;

namespace Whalefleet.Application.Abstractions.Terminal;

/// <summary>
/// 终端会话计数器：限制<b>单个用户</b>同时持有的会话数。
/// </summary>
/// <remarks>
/// <para><b>为什么需要上限</b>：每条会话在容器里是一个真实进程、在服务端是一条 WebSocket
/// 加一条到守护进程的 hijacked 连接。这个代价与「点了几下」无关，只与「开了几个」有关，
/// 因此必须有一个与用户绑定的闸门——否则一个循环打开终端的脚本就能把容器的进程表撑爆，
/// 而受害者是那个容器里真正在跑的业务。</para>
/// <para><b>为什么按用户而不是按连接</b>：一条 WebSocket 就是一个会话，按连接计数与按会话计数相同，
/// 但「用户」才是我们想限制的主体（同一个人可以开多个浏览器窗口）。</para>
/// <para><b>为什么是 Singleton</b>：计数必须在所有请求、所有连接之间共享。
/// 注册成 Scoped 会让每个连接各数各的，等于没有上限。</para>
/// <para><b>为什么不落库</b>：会话是内存里的活对象，进程重启后连接也一并没了。
/// 把「当前有几条会话」写进数据库只会引入一个需要回收的脏状态。</para>
/// <para><b>为什么放在应用层</b>：它是「每人最多几条会话」这条<b>策略</b>的实现，
/// 与 HTTP / WebSocket 无关；放在 Api 层会让它无法被单元测试覆盖
/// （<c>Whalefleet.UnitTests</c> 不允许引用 Api）。</para>
/// </remarks>
public sealed class TerminalSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    /// <summary>
    /// 尝试占用一个会话名额。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="limit">该用户允许的最大会话数。</param>
    /// <param name="lease">占用成功时返回的租约；失败时为 <see langword="null"/>。</param>
    /// <returns>占用成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>释放责任在调用方</b>：租约必须在 <c>finally</c> 里 <c>Dispose</c>，
    /// 否则名额会永久泄漏——症状是「用过几次之后再也打不开终端，重启服务才好」。
    /// </remarks>
    public bool TryAcquire(Guid userId, int limit, out IDisposable? lease)
    {
        lease = null;

        // 上限非正数时一律拒绝，且**不碰字典**：先自增再拒绝会留下一个永远不会释放的名额，
        // 表现为「这条连接被拒了，但之后同一个人再也开不了终端」。
        if (limit <= 0)
        {
            return false;
        }

        // 用 AddOrUpdate 的更新委托做「检查 + 自增」，避免「先读后写」的竞态：
        // 返回原值时表示超限（不写入），返回新值时表示已占用。
        var rejected = false;
        _counts.AddOrUpdate(
            userId,
            static _ => 1,
            (_, current) =>
            {
                if (current >= limit)
                {
                    rejected = true;
                    return current;
                }

                return current + 1;
            });

        if (rejected)
        {
            return false;
        }

        lease = new SessionLease(this, userId);

        return true;
    }

    /// <summary>
    /// 读取某个用户当前持有的会话数（仅供日志与测试断言）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <returns>当前会话数。</returns>
    public int CountFor(Guid userId) =>
        _counts.TryGetValue(userId, out var count) ? count : 0;

    private void Release(Guid userId)
    {
        // 减到 0 就删键：否则字典会随「历史上的每个用户」单调增长（软删除的用户也算）。
        while (true)
        {
            if (!_counts.TryGetValue(userId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                if (_counts.TryRemove(new KeyValuePair<Guid, int>(userId, current)))
                {
                    return;
                }

                continue;
            }

            if (_counts.TryUpdate(userId, current - 1, current))
            {
                return;
            }
        }
    }

    /// <summary>
    /// 一条会话名额的租约（幂等释放）。
    /// </summary>
    private sealed class SessionLease : IDisposable
    {
        private readonly TerminalSessionRegistry _owner;
        private readonly Guid _userId;
        private int _released;

        public SessionLease(TerminalSessionRegistry owner, Guid userId)
        {
            _owner = owner;
            _userId = userId;
        }

        public void Dispose()
        {
            // 幂等：正常路径与异常路径都可能调到，重复释放会把别人的名额也减掉。
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            _owner.Release(_userId);
        }
    }
}
