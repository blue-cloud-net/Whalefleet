using Docker.DotNet.Models;
using FluentAssertions;
using Whalefleet.Application.Volumes;
using Xunit;

namespace Whalefleet.UnitTests.Volumes;

/// <summary>
/// <see cref="VolumeUsageCache"/> 的契约。
/// </summary>
/// <remarks>
/// 缓存存在的唯一理由是「<c>/system/df</c> 一次要几秒」（实测 216 个卷 3.7 秒），
/// 因此这里守两件事：<b>窗口内真的复用</b>（否则白做），以及<b>过期后真的重新取</b>
/// （否则删卷后的数字会一直停在旧值上）。
/// </remarks>
public sealed class VolumeUsageCacheTests
{
    /// <summary>可手动推进的时间源：缓存是按 <see cref="TimeProvider"/> 判过期的。</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static Dictionary<string, UsageData> Usage(long size, long refCount) =>
        new Dictionary<string, UsageData>(StringComparer.Ordinal)
        {
            ["v"] = new UsageData { Size = size, RefCount = refCount },
        };

    [Fact]
    public void 未写入时不命中()
    {
        var cache = new VolumeUsageCache(new MutableTimeProvider());

        var hit = cache.TryGet("local", out var usage);

        hit.Should().BeFalse();
        usage.Should().BeEmpty("「不知道」必须是空字典，而不是伪造一个 0");
    }

    [Fact]
    public void 写入后窗口内命中同一份数据()
    {
        var time = new MutableTimeProvider();
        var cache = new VolumeUsageCache(time);
        var expected = Usage(1024, 2);

        cache.Set("local", expected);
        time.Advance(TimeSpan.FromSeconds(29));

        cache.TryGet("local", out var usage).Should().BeTrue();
        usage.Should().BeSameAs(expected);
    }

    [Fact]
    public void 超过有效期后重新视为未命中()
    {
        var time = new MutableTimeProvider();
        var cache = new VolumeUsageCache(time);

        cache.Set("local", Usage(1024, 2));
        time.Advance(TimeSpan.FromSeconds(30));

        // 到期即失效：删除卷后不该继续显示旧数字
        cache.TryGet("local", out var usage).Should().BeFalse();
        usage.Should().BeEmpty();
    }

    [Fact]
    public void 主机名不区分大小写()
    {
        // hostId 就是主机名，而主机名比较在本项目里一律不区分大小写（见 AGENTS.md）。
        // 用 Ordinal 会让 Local / local 各缓存一份，等于缓存白做。
        var cache = new VolumeUsageCache(new MutableTimeProvider());

        cache.Set("Local", Usage(1024, 2));

        cache.TryGet("local", out _).Should().BeTrue();
    }

    [Fact]
    public void 不同主机互不影响()
    {
        var cache = new VolumeUsageCache(new MutableTimeProvider());

        cache.Set("local", Usage(1024, 2));

        cache.TryGet("prod-01", out _).Should().BeFalse();
    }
}
