using Docker.DotNet.Models;

namespace Whalefleet.Application.Images;

/// <summary>
/// 把 Docker 的进度消息聚合成「总体百分比」。
/// </summary>
/// <remarks>
/// <para><b>为什么不能直接用单层的进度</b>：Docker 的进度流是<b>逐层</b>给的
/// （<c>Downloading [====&gt;   ] 12.3MB/40MB</c>），每层各有一条进度。
/// 直接把某一层的百分比当总体百分比，用户会看到「进度条在 30% 和 0% 之间反复横跳」——
/// 因为层是按顺序开始的，前一层完成后总体还没推进多少，下一层又从 0% 开始。</para>
/// <para><b>为什么百分比可能是「不知道」</b>：<b>总层数事先不知道</b>——
/// 它随镜像仓库返回的内容变化，守护进程在流开始前也说不准。
/// 因此在拿到全部分层之前，算不出总体百分比。这种时候必须显式说「不知道」，
/// 而不能给 0（那是「确实是 0」）或 50（那是编造的数字）。</para>
/// <para><b>为什么按层累加而不是取最大值</b>：层之间有先后，
/// 「取最大」会让进度在层切换时回退。累加（已完成的层按其总量计入）才能单调前进。</para>
/// </remarks>
public sealed class ImageProgressAggregator
{
    /// <summary>
    /// 各层的最新进度（键 = 层 ID）。
    /// </summary>
    private readonly Dictionary<string, (long Current, long Total)> _layers = new(StringComparer.Ordinal);

    /// <summary>
    /// 最近一条状态原文。
    /// </summary>
    private string? _lastStatus;

    /// <summary>
    /// 总体百分比（0–100）；无法确定时为 <see langword="null"/>。
    /// </summary>
    public double? Percent
    {
        get
        {
            long current = 0;
            long total = 0;

            foreach (var layer in _layers.Values)
            {
                current += layer.Current;
                total += layer.Total;
            }

            if (total <= 0)
            {
                return null;
            }

            var percent = (double)current / total * 100;

            return Math.Clamp(percent, 0, 100);
        }
    }

    /// <summary>
    /// 最近一条状态原文（守护进程给的，可直接展示）。
    /// </summary>
    public string? LastStatus => _lastStatus;

    /// <summary>
    /// 吃进一条进度消息。
    /// </summary>
    /// <param name="message">进度消息。</param>
    /// <remarks>
    /// 没有层 ID 的消息（各种状态说明）只更新「最近状态」，
    /// 不参与百分比计算——它们不代表任何一层的进度。
    /// </remarks>
    public void Update(JSONMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.IsNullOrWhiteSpace(message.Status))
        {
            _lastStatus = message.Status;
        }

        if (string.IsNullOrWhiteSpace(message.ID) || message.Progress is null)
        {
            return;
        }

        var current = message.Progress.Current ?? 0;
        var total = message.Progress.Total ?? 0;

        if (total <= 0)
        {
            return;
        }

        this._layers[message.ID] = (current, total);
    }
}

/// <summary>
/// 进度发布的节流器。
/// </summary>
/// <remarks>
/// <para><b>为什么要节流</b>：Docker 的进度流非常密集（每层每秒好几条），
/// 逐条推给浏览器会把它变成「每秒几十帧」。而进度条本身根本看不出那么细的差别。</para>
/// <para><b>为什么用时间而不是条数</b>：条数阈值在「日志很多但进度很少」时会让界面长时间不更新，
/// 而时间阈值能保证「最多延迟 N 毫秒」。</para>
/// </remarks>
public sealed class ProgressPublishThrottle
{
    /// <summary>
    /// 最小发布间隔。
    /// </summary>
    private readonly TimeSpan _interval;

    /// <summary>
    /// 上次发布时刻。
    /// </summary>
    private long _lastTicks;

    /// <summary>
    /// 初始化节流器。
    /// </summary>
    /// <param name="interval">最小发布间隔。</param>
    public ProgressPublishThrottle(TimeSpan interval)
    {
        _interval = interval;
    }

    /// <summary>
    /// 判断现在是否该发布。
    /// </summary>
    /// <returns>该发布返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>首次调用总是返回 true</b>：用户点下「拉取」后应当<b>立刻</b>看到反馈，
    /// 而不是等到 200 毫秒后才有动静。</remarks>
    public bool ShouldPublish()
    {
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastTicks);

        if (last == 0)
        {
            Interlocked.Exchange(ref _lastTicks, now);
            return true;
        }

        if (now - last < (long)_interval.TotalMilliseconds)
        {
            return false;
        }

        // ⚠️ 只有**真的发布时**才更新时刻：否则每次检查都把时刻推后，
        // 密集检查下就永远达不到间隔（进度条会一直停在 0%）。
        Interlocked.Exchange(ref _lastTicks, now);

        return true;
    }
}
