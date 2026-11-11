using System.Threading.Channels;

using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Application.Common;

/// <summary>
/// 进程内通知总线（基于 <see cref="Channel{T}"/>）。
/// </summary>
/// <remarks>
/// 与 <see cref="InProcessContainerEventBus"/> 同构：每个订阅方独立通道，
/// 满了丢最旧，发布方永不阻塞。
/// </remarks>
public sealed class InProcessNotificationBus : INotificationBus
{
    private const int _subscriberCapacity = 256;

    private readonly object _gate = new();

    private readonly List<Channel<Notification>> _subscribers = [];

    /// <inheritdoc />
    public ValueTask PublishAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        List<Channel<Notification>> targets;

        lock (_gate)
        {
            targets = [.. _subscribers];
        }

        foreach (var channel in targets)
        {
            channel.Writer.TryWrite(notification);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Notification> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<Notification>(
            new BoundedChannelOptions(_subscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        }
    }
}
