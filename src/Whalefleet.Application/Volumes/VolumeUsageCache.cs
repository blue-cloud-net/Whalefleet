using Docker.DotNet.Models;

namespace Whalefleet.Application.Volumes;

/// <summary>
/// 卷用量的短时缓存（**单例**）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须缓存</b>：用量只能从 <c>GET /system/df?type=volume</c> 取，
/// 而那个端点会**逐个卷统计占用**（实测：216 个卷、其中一个 94 GB 的 buildkit 状态，
/// 单次调用 3.7 秒；卷列表本身只要 1 毫秒）。列表接口一旦带上用量，
/// 翻页、刷新、换每页条数都会各付一次这个成本——分页带来的「快」会被它整个吃掉。</para>
/// <para><b>为什么可以在短窗口内是陈旧的</b>：占用与引用数只用于展示，不参与任何判定；
/// 30 秒内用户看到的数字差一点点，远比每次翻页等 4 秒好。真正的删除安全性由守护进程
/// 在删除时判定（<c>1404</c>），不依赖这里。</para>
/// <para><b>失败不缓存</b>：用量查询失败时返回的是「不知道」（<c>null</c>），
/// 把它缓存 30 秒会让一次瞬时抖动变成半分钟的空白列。下一次请求应当重试。</para>
/// <para><b>按主机名做键</b>：<c>hostId</c> 就是主机名且不区分大小写，
/// 因此比较器用 <see cref="StringComparer.OrdinalIgnoreCase"/>——否则 <c>Local</c>
/// 与 <c>local</c> 会各缓存一份，等于缓存失效。</para>
/// </remarks>
public sealed class VolumeUsageCache
{    /// <summary>缓存有效期。</summary>
    /// <remarks>
    /// 30 秒是「用户连续翻页 / 点一次刷新」与「删除卷后数字立刻变」之间的折中：
    /// 比这更短会让分页重新变得值不回票价，更长则会让删除后的数字明显滞后。
    /// </remarks>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 初始化卷用量缓存。
    /// </summary>
    /// <param name="timeProvider">时间源（便于测试）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> 为 <see langword="null"/>。</exception>
    public VolumeUsageCache(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 尝试取某台主机的用量快照。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="usage">命中的用量（卷名 → 用量）；未命中时为空字典。</param>
    /// <returns>命中且未过期返回 <see langword="true"/>。</returns>
    public bool TryGet(string hostId, out IReadOnlyDictionary<string, UsageData> usage)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_entries.TryGetValue(hostId, out var entry)
                && now - entry.FetchedAt < TimeToLive)
            {
                usage = entry.Usage;

                return true;
            }

            // 过期条目顺手删掉：键的数量上限是主机数，不做后台清理也不会无限增长
            _entries.Remove(hostId);
        }

        usage = Empty;

        return false;
    }

    /// <summary>
    /// 写入某台主机的用量快照。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="usage">用量（卷名 → 用量）。</param>
    public void Set(string hostId, IReadOnlyDictionary<string, UsageData> usage)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            _entries[hostId] = new Entry(usage, now);
        }
    }

    /// <summary>空用量：<b>不是</b>「用量为 0」，而是「不知道」。</summary>
    private static readonly IReadOnlyDictionary<string, UsageData> Empty =
        new Dictionary<string, UsageData>(0, StringComparer.Ordinal);

    /// <summary>一条缓存项。</summary>
    /// <param name="Usage">用量。</param>
    /// <param name="FetchedAt">写入时刻（UTC）。</param>
    private readonly record struct Entry(IReadOnlyDictionary<string, UsageData> Usage, DateTimeOffset FetchedAt);
}
