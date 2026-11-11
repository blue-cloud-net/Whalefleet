using Whalefleet.Core.Alerting;

namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 站内通知总线（进程内发布 / 订阅）。
/// </summary>
/// <remarks>
/// <para>落库后的通知要推给在线浏览器，而「推 Hub」是 Api 层的事。
/// 应用层只发布事实，Api 的派发器订阅并逐连接判可见性。</para>
/// <para><b>不阻塞发布方</b>：发布方可能是告警评估器或容器事件派发器，
/// 慢订阅方不能拖住它们。</para>
/// </remarks>
public interface INotificationBus
{
    /// <summary>
    /// 发布一条通知。
    /// </summary>
    ValueTask PublishAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅通知。
    /// </summary>
    IAsyncEnumerable<Notification> SubscribeAsync(CancellationToken cancellationToken = default);
}
