using Whalefleet.Application.Abstractions;
using Whalefleet.Infrastructure.Security;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Whalefleet.IntegrationTests.Security;

/// <summary>
/// <c>InMemoryLoginAttemptTracker</c> 的限流行为测试。
/// </summary>
/// <remarks>
/// <para>窗口判定用的是注入的假时钟，因此「锁死之后能否自动解封」这一条可以<b>确定性地</b>断言——
/// 它恰恰是最容易静默失效的行为：若窗口永不重置，一次误输就会把账号永久锁死。</para>
/// <para>这里直接构造具体类型（而非从 DI 解析）是刻意的：本类型的测试重点是窗口算术，
/// 用真实时钟需要等 60 秒，用容器反而引入无关的注册依赖。</para>
/// </remarks>
public class InMemoryLoginAttemptTrackerTests
{
    private const string _ip = "203.0.113.7";
    private const string _user = "admin";

    private static readonly DateTimeOffset _now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 未达上限时不封锁_达到上限后封锁()
    {
        var (tracker, _) = Create();

        for (var i = 1; i < InMemoryLoginAttemptTracker.MaxFailuresPerWindow; i++)
        {
            tracker.RecordFailure(_ip, _user);
            tracker.IsBlocked(_ip, _user).Should().BeFalse($"{i} 次失败尚未达到上限");
        }

        tracker.RecordFailure(_ip, _user).Should().Be(InMemoryLoginAttemptTracker.MaxFailuresPerWindow);
        tracker.IsBlocked(_ip, _user).Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_返回窗口内累计次数()
    {
        var (tracker, _) = Create();

        tracker.RecordFailure(_ip, _user).Should().Be(1);
        tracker.RecordFailure(_ip, _user).Should().Be(2);
        tracker.RecordFailure(_ip, _user).Should().Be(3);
    }

    [Fact]
    public void 用户名不区分大小写_共用同一计数()
    {
        var (tracker, _) = Create();

        for (var i = 0; i < InMemoryLoginAttemptTracker.MaxFailuresPerWindow; i++)
        {
            // 交替使用不同大小写：若按原样计数，就无法达到上限。
            tracker.RecordFailure(_ip, i % 2 == 0 ? "Admin" : "ADMIN");
        }

        tracker.IsBlocked(_ip, "admin").Should().BeTrue();
    }

    [Fact]
    public void 不同IP或不同用户名_互不影响()
    {
        var (tracker, _) = Create();

        for (var i = 0; i < InMemoryLoginAttemptTracker.MaxFailuresPerWindow; i++)
        {
            tracker.RecordFailure(_ip, _user);
        }

        tracker.IsBlocked(_ip, _user).Should().BeTrue();

        // 换 IP：不该被牵连（否则 NAT 后的正常用户会互相拖累）。
        tracker.IsBlocked("198.51.100.9", _user).Should().BeFalse();

        // 换用户名：不该被牵连。
        tracker.IsBlocked(_ip, "someone-else").Should().BeFalse();
    }

    [Fact]
    public void Reset_清零后不再封锁()
    {
        var (tracker, _) = Create();

        for (var i = 0; i < InMemoryLoginAttemptTracker.MaxFailuresPerWindow; i++)
        {
            tracker.RecordFailure(_ip, _user);
        }

        tracker.IsBlocked(_ip, _user).Should().BeTrue();

        tracker.Reset(_ip, _user);

        tracker.IsBlocked(_ip, _user).Should().BeFalse();
        tracker.RecordFailure(_ip, _user).Should().Be(1, "清零后重新从 1 开始计数");
    }

    [Fact]
    public void 窗口过期后自动解封()
    {
        var (tracker, clock) = Create();

        for (var i = 0; i < InMemoryLoginAttemptTracker.MaxFailuresPerWindow; i++)
        {
            tracker.RecordFailure(_ip, _user);
        }

        tracker.IsBlocked(_ip, _user).Should().BeTrue();

        // 差一点点还没到窗口：仍应封锁。
        clock.Advance(InMemoryLoginAttemptTracker.Window - TimeSpan.FromSeconds(1));
        tracker.IsBlocked(_ip, _user).Should().BeTrue();

        // 越过窗口：自动解封（否则一次误输会把账号永久锁死）。
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.IsBlocked(_ip, _user).Should().BeFalse();
        tracker.RecordFailure(_ip, _user).Should().Be(1);
    }

    [Fact]
    public void 持续失败不会顺延窗口()
    {
        var (tracker, clock) = Create();

        // 每过半分钟失败一次：若窗口按"最后一次失败"起算，就永远不会解封。
        for (var i = 0; i < 4; i++)
        {
            tracker.RecordFailure(_ip, _user);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        // 距第一次失败已过 2 分钟 > 窗口长度，故计数应已重置。
        tracker.IsBlocked(_ip, _user).Should().BeFalse();
        tracker.RecordFailure(_ip, _user).Should().Be(1);
    }

    private static (ILoginAttemptTracker Tracker, TestTimeProvider Clock) Create()
    {
        var clock = new TestTimeProvider(_now);
        var cache = new MemoryCache(new MemoryCacheOptions());

        return (new InMemoryLoginAttemptTracker(cache, clock), clock);
    }
}
