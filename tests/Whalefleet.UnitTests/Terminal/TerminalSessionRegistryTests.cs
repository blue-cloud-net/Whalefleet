using Whalefleet.Application.Abstractions.Terminal;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Terminal;

/// <summary>
/// 终端会话计数器（0.8.0）。
/// </summary>
/// <remarks>
/// 这里最容易错的两件事都不是「数不准」，而是<b>泄漏</b>：
/// 名额被拒绝时却已经自增（之后同一个人再也开不了终端），
/// 以及租约被重复释放（把别人的名额也减掉）。
/// </remarks>
public class TerminalSessionRegistryTests
{
    private readonly TerminalSessionRegistry _registry = new();

    [Fact]
    public void 超过上限后拒绝且不占用名额()
    {
        var userId = Guid.NewGuid();

        var first = _registry.TryAcquire(userId, 1, out var lease1);
        var second = _registry.TryAcquire(userId, 1, out var lease2);

        first.Should().BeTrue();
        lease1.Should().NotBeNull();

        second.Should().BeFalse();
        lease2.Should().BeNull();

        // 被拒的那次不能留下痕迹：否则释放一个名额后仍然开不了第二个。
        _registry.CountFor(userId).Should().Be(1);

        lease1!.Dispose();
        _registry.CountFor(userId).Should().Be(0);
    }

    [Fact]
    public void 释放后可以再次占用()
    {
        var userId = Guid.NewGuid();

        _registry.TryAcquire(userId, 1, out var lease);
        lease!.Dispose();

        _registry.TryAcquire(userId, 1, out var again).Should().BeTrue();
        again!.Dispose();
    }

    [Fact]
    public void 租约重复释放不会减到别人的名额()
    {
        var userId = Guid.NewGuid();

        _registry.TryAcquire(userId, 5, out var first);
        _registry.TryAcquire(userId, 5, out var second);

        first!.Dispose();
        first.Dispose();

        _registry.CountFor(userId).Should().Be(1);

        second!.Dispose();
        _registry.CountFor(userId).Should().Be(0);
    }

    [Fact]
    public void 用户之间互不影响()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        _registry.TryAcquire(alice, 1, out var aliceLease);
        _registry.TryAcquire(bob, 1, out var bobLease).Should().BeTrue();

        aliceLease!.Dispose();
        bobLease!.Dispose();
    }

    [Fact]
    public void 上限非正数时一律拒绝()
    {
        _registry.TryAcquire(Guid.NewGuid(), 0, out var lease).Should().BeFalse();
        lease.Should().BeNull();
    }

    [Fact]
    public void 计数归零后不残留键()
    {
        var userId = Guid.NewGuid();

        _registry.TryAcquire(userId, 1, out var lease);
        lease!.Dispose();

        // 字典不该随「历史上的每个用户」单调增长。
        _registry.CountFor(userId).Should().Be(0);
    }
}
