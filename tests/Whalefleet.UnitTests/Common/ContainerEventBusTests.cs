using FluentAssertions;

using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;

using Xunit;

namespace Whalefleet.UnitTests.Common;

/// <summary>
/// 已取消的发布不能把事件交给订阅者。
/// </summary>
public sealed class ContainerEventBusTests
{
    [Fact]
    public async Task 发布_令牌已取消_不投递事件()
    {
        var bus = new InProcessContainerEventBus();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = bus.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        var cancelled = new ContainerEventNotification("local", "cancelled", "cancelled", 1, false, DateTimeOffset.UtcNow);
        var act = async () => await bus.PublishAsync(cancelled, new CancellationToken(canceled: true));
        await act.Should().ThrowAsync<OperationCanceledException>();
        var accepted = cancelled with
        {
            ContainerId = "accepted"
        };
        await bus.PublishAsync(accepted);
        (await pending).Should().BeTrue();
        reader.Current.ContainerId.Should().Be("accepted");
    }
}
