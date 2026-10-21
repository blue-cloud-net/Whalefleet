using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 容器资源采样实时推送 Hub（主机作用域）。
/// </summary>
/// <remarks>
/// <para><b>路径</b>：<c>/hubs/hosts/{hostId}/container-stats</c>。</para>
/// <para><b>为什么单独一个 Hub 而不是复用日志 Hub</b>：两者的频率、生命周期与背压完全不同——
/// 日志是「有就推、可能很密」，采样是「固定节奏、永远不空」。
/// 合成一个流会让客户端必须同时处理「可能十秒没有一帧」与「一秒十帧」两种节奏。</para>
/// <para><b>鉴权两层</b>：连接建立时中间件校验主机可见性；每次订阅时本 Hub 校验
/// <c>container.read</c>（与日志 Hub 同一口径，理由见该类的说明）。</para>
/// <para><b>消费侧归 0.6.0</b>：本 Hub 只负责「把数据推到客户端」，
/// 指标列、健康检测、订阅调度那些「怎么看」的能力不在这里。</para>
/// </remarks>
[Authorize]
public sealed class ContainerStatsHub : Hub
{
    /// <summary>
    /// 订阅类别名（用于按类别计数）。
    /// </summary>
    public const string SubscriptionKind = "stats";

    private readonly RealtimeHostResolver _resolver;
    private readonly RealtimeSubscriptionRegistry _subscriptions;
    private readonly IHubContext<ContainerStatsHub> _hubContext;
    private readonly IContainerStatsSampler _sampler;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<ContainerStatsHub> _logger;

    /// <summary>
    /// 初始化统计 Hub。
    /// </summary>
    /// <param name="resolver">主机解析与权限判定。</param>
    /// <param name="subscriptions">订阅注册表。</param>
    /// <param name="hubContext">Hub 上下文（后台推送用）。</param>
    /// <param name="sampler">进程内共享的采样器（一容器一条 Docker 流）。</param>
    /// <param name="options">实时通道选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerStatsHub(
        RealtimeHostResolver resolver,
        RealtimeSubscriptionRegistry subscriptions,
        IHubContext<ContainerStatsHub> hubContext,
        IContainerStatsSampler sampler,
        IOptions<RealtimeOptions> options,
        ILogger<ContainerStatsHub> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _resolver = resolver;
        _subscriptions = subscriptions;
        _hubContext = hubContext;
        _sampler = sampler;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 订阅容器资源采样。
    /// </summary>
    /// <param name="containerId">容器标识（完整 ID / 短 ID / 名称）。</param>
    /// <returns>订阅结果；失败时带错误码与消息。</returns>
    public async Task<RealtimeActionResult> SubscribeStats(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return new RealtimeActionResult(false, ErrorCodes.ValidationFailed, "容器标识不能为空。");
        }

        DockerHost host;

        try
        {
            host = await _resolver
                .ResolveAsync(this.Context, Permission.ContainerRead, this.Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (BusinessException exception)
        {
            _logger.LogWarning(
                "订阅容器采样被拒（连接={ConnectionId}，容器={ContainerId}，Code={Code}）：{Reason}",
                this.Context.ConnectionId,
                containerId,
                exception.Code,
                exception.Message);

            return RealtimeActionResult.FromError(exception);
        }

        var subscription = _subscriptions.TryRegister(
            this.Context.ConnectionId,
            SubscriptionKind,
            containerId,
            _options.Value.MaxStatsSubscriptionsPerConnection);

        if (subscription is null)
        {
            var count = _subscriptions.CountPerKind(this.Context.ConnectionId, SubscriptionKind);
            var limit = _options.Value.MaxStatsSubscriptionsPerConnection;

            return new RealtimeActionResult(
                false,
                ErrorCodes.ValidationFailed,
                count >= limit
                    ? $"单个连接最多订阅 {limit} 个容器的采样，请先退订其它容器。"
                    : "该容器的采样已在订阅中。");
        }

        subscription.Pump = this.StartPump(subscription, host.Name, containerId);

        _logger.LogInformation(
            "订阅容器采样（连接={ConnectionId}，主机={HostId}，容器={ContainerId}）",
            this.Context.ConnectionId,
            host.Name,
            containerId);

        return RealtimeActionResult.Ok;
    }

    /// <summary>
    /// 退订容器资源采样。
    /// </summary>
    /// <param name="containerId">容器标识。</param>
    /// <returns>退订结果。</returns>
    public async Task<RealtimeActionResult> UnsubscribeStats(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return new RealtimeActionResult(false, ErrorCodes.ValidationFailed, "容器标识不能为空。");
        }

        await _subscriptions
            .RemoveAndStopAsync(this.Context.ConnectionId, SubscriptionKind, containerId)
            .ConfigureAwait(false);

        return RealtimeActionResult.Ok;
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _subscriptions.StopAllAsync(this.Context.ConnectionId).ConfigureAwait(false);

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动后台推送任务。
    /// </summary>
    /// <param name="subscription">订阅。</param>
    /// <param name="hostId">主机标识。</param>
    /// <param name="containerId">容器标识。</param>
    /// <returns>推送任务。</returns>
    private Task StartPump(RealtimeSubscription subscription, string hostId, string containerId)
    {
        var connectionId = this.Context.ConnectionId;
        var logger = _logger;
        var hubContext = _hubContext;
        var sampler = _sampler;

        return Task.Run(async () =>
        {
            // 采样器是进程内共享的：同一个容器无论多少订阅者，只占一条到 Docker 的流。
            // 订阅时它会先给一段历史快照（环形缓冲），因此新打开的页面不必等一分钟才有图。
            await using var source = sampler.Subscribe(hostId, containerId);

            try
            {
                if (source.History.Count > 0)
                {
                    await hubContext.Clients
                        .Client(connectionId)
                        .SendAsync(
                            "OnStatsHistory",
                            new ContainerStatsHistoryPayload(containerId, source.History),
                            subscription.Cancellation.Token)
                        .ConfigureAwait(false);
                }

                await foreach (var sample in source
                    .ReadAllAsync(subscription.Cancellation.Token)
                    .ConfigureAwait(false))
                {
                    await hubContext.Clients
                        .Client(connectionId)
                        .SendAsync("OnStats", new ContainerStatsPayload(containerId, sample))
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 退订或断开：预期路径。
            }
            catch (BusinessException exception)
            {
                logger.LogWarning(
                    "容器采样流失败（连接={ConnectionId}，容器={ContainerId}，Code={Code}）：{Reason}",
                    connectionId,
                    containerId,
                    exception.Code,
                    exception.Message);

                await SendErrorAsync(hubContext, connectionId, exception.Code, exception.Message, logger)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "容器采样流异常（连接={ConnectionId}，容器={ContainerId}）",
                    connectionId,
                    containerId);

                await SendErrorAsync(
                    hubContext,
                    connectionId,
                    ErrorCodes.DockerApiFailed,
                    $"读取容器采样失败：{exception.Message}",
                    logger).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// 推送错误。
    /// </summary>
    /// <param name="hubContext">Hub 上下文。</param>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">消息。</param>
    /// <param name="logger">日志。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task SendErrorAsync(
        IHubContext<ContainerStatsHub> hubContext,
        string connectionId,
        int code,
        string message,
        ILogger logger)
    {
        try
        {
            await hubContext.Clients
                .Client(connectionId)
                .SendAsync("OnStreamError", new RealtimeErrorPayload(code, message))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                "推送错误通知失败（连接={ConnectionId}）：{Reason}",
                connectionId,
                exception.Message);
        }
    }
}
