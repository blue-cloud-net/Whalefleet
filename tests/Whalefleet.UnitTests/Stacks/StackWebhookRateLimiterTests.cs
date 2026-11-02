using FluentAssertions;

using Whalefleet.Application.Stacks;

using Xunit;

namespace Whalefleet.UnitTests.Stacks;

/// <summary>
/// Webhook 限流的并发与时间边界回归。
/// </summary>
public sealed class StackWebhookRateLimiterTests
{
    [Fact]
    public async Task 受理_并发触发_同一窗口最多十次()
    {
        var limiter = new StackWebhookRateLimiter(TimeProvider.System);
        var stackId = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => limiter.TryAcquire(stackId))));
        results.Count(value => value).Should().Be(10);
        limiter.TryAcquire(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void 受理_窗口到期_被拒请求不延长窗口()
    {
        var clock = new ManualClock();
        var limiter = new StackWebhookRateLimiter(clock);
        var stackId = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
        {
            limiter.TryAcquire(stackId).Should().BeTrue();
        }

        clock.Advance(TimeSpan.FromSeconds(59));
        limiter.TryAcquire(stackId).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquire(stackId).Should().BeTrue();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
