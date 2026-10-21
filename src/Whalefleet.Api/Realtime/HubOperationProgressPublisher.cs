using Microsoft.AspNetCore.SignalR;
using Whalefleet.Application.Abstractions.Realtime;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 通过 <see cref="NotificationHub"/> 推送镜像操作进度。
/// </summary>
/// <remarks>
/// <para><b>为什么实现在 Api 层</b>：进度要送到客户端，而「怎么送」是入站适配器的职责——
/// 应用层不该知道 SignalR 的存在。接口 <see cref="IOperationProgressPublisher"/> 留在应用层，
/// 实现放在这里并注册成同一个服务。</para>
/// <para><b>为什么不抛异常</b>：进度推送失败（客户端已断开、连接被重置）不能让拉取或构建本身失败。
/// 那些操作已经在 Docker 侧执行，失败的是「进度没送到」——那只是界面少了个进度条。</para>
/// </remarks>
internal sealed class HubOperationProgressPublisher : IOperationProgressPublisher, IDisposable
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly RealtimeSubscriptionRegistry _subscriptions;
    private readonly ILogger<HubOperationProgressPublisher> _logger;

    /// <summary>
    /// 推送的串行化信号量。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要</b>：进度是从 Docker 的读取回调里 fire-and-forget 发出的，
    /// 两次 <c>SendAsync</c> 可能**交错完成**——客户端会看到「90% 之后又出现 40%」。
    /// 用一把信号量把推送串起来，顺序就与产生顺序一致了。
    /// <br/>
    /// 进度量很小（每秒几次），串行化不会成为瓶颈。
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 初始化发布器。
    /// </summary>
    /// <param name="hubContext">通知 Hub 上下文。</param>
    /// <param name="subscriptions">订阅注册表（按令牌反查订阅者）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HubOperationProgressPublisher(
        IHubContext<NotificationHub> hubContext,
        RealtimeSubscriptionRegistry subscriptions,
        ILogger<HubOperationProgressPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(logger);

        _hubContext = hubContext;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        ImageOperationProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var targets = _subscriptions.FindByResource(
            NotificationHub.PullSubscriptionKind,
            progress.Token);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var subscription in targets)
            {
                try
                {
                    await _hubContext.Clients
                        .Client(subscription.ConnectionId)
                        .SendAsync("OnPullProgress", progress, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 客户端已断开：进度推不出去而已，操作本身照常进行。
                    _logger.LogDebug(
                        "推送镜像操作进度失败（连接={ConnectionId}，令牌={Token}）：{Reason}",
                        subscription.ConnectionId,
                        progress.Token,
                        exception.Message);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
