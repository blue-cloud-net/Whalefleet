using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 容器日志实时推送 Hub（主机作用域）。
/// </summary>
/// <remarks>
/// <para><b>路径</b>：<c>/hubs/hosts/{hostId}/container-log</c>。
/// 主机在<b>路径</b>里而不是请求体里，与本项目其余端点一致。</para>
/// <para><b>鉴权两层</b>：<list type="number">
/// <item><description>连接建立时，中间件校验主机可见性（<c>host.read</c>）；</description></item>
/// <item><description>每次订阅时，本 Hub 校验 <c>container.read</c>（本主机作用域）。</description></item>
/// </list>
/// 只做第一层是不够的——「看得见这台机器」不等于「能读它的容器日志」。</para>
/// <para><b>⚠️ 这里比现有 REST 端点更严</b>：<c>POST /containers/logs</c> 目前只经中间件的
/// <c>host.read</c> 校验，而本 Hub 额外要求 <c>container.read</c>。
/// 权限目录里两者是<b>不同</b>的权限（<c>host.read</c> 是「看得见这台机器」，
/// <c>container.read</c> 才是「能看这台机器上的容器」），
/// 而 REST 端点尚未接上资源级判定——那是 0.12.0「授权下沉到容器级」要一并收口的事。
/// 这里是<b>新功能</b>，从严不构成回退；两处口径统一记在 0.12.0 的待办里。</para>
/// <para><b>为什么推送任务不能直接用注入的 Scoped 服务</b>：Hub 实例每次调用都是新的，
/// 方法返回后就被释放。推送任务是<b>长期</b>的，必须自己开一个依赖注入作用域
/// （<see cref="IServiceScopeFactory"/>），否则它持有的服务会在方法返回后被一起释放。</para>
/// </remarks>
[Authorize]
public sealed class ContainerLogHub : Hub
{
    /// <summary>
    /// 订阅类别名（用于按类别计数）。
    /// </summary>
    public const string SubscriptionKind = "log";

    private readonly RealtimeHostResolver _resolver;
    private readonly RealtimeSubscriptionRegistry _subscriptions;
    private readonly IHubContext<ContainerLogHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<ContainerLogHub> _logger;

    /// <summary>
    /// 初始化日志 Hub。
    /// </summary>
    /// <param name="resolver">主机解析与权限判定。</param>
    /// <param name="subscriptions">订阅注册表。</param>
    /// <param name="hubContext">Hub 上下文（后台推送用）。</param>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="options">实时通道选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerLogHub(
        RealtimeHostResolver resolver,
        RealtimeSubscriptionRegistry subscriptions,
        IHubContext<ContainerLogHub> hubContext,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeOptions> options,
        ILogger<ContainerLogHub> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _resolver = resolver;
        _subscriptions = subscriptions;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 订阅容器日志。
    /// </summary>
    /// <param name="containerId">容器标识（完整 ID / 短 ID / 名称）。</param>
    /// <param name="startSeq">起始行序号（首屏历史有 N 行时传 N+1）。</param>
    /// <param name="since">起始时刻（Unix 秒）；只推该时刻之后的行。</param>
    /// <returns>订阅结果；失败时带错误码与消息。</returns>
    public async Task<RealtimeActionResult> SubscribeLogs(
        string containerId,
        int startSeq = 1,
        long? since = null)
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
                "订阅容器日志被拒（连接={ConnectionId}，容器={ContainerId}，Code={Code}）：{Reason}",
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
            _options.Value.MaxSubscriptionsPerKind);

        if (subscription is null)
        {
            // 两种可能：已订阅同一容器（重复调用）或超出每类上限。
            // 两者都不该再开一条 Docker 长连接。
            var count = _subscriptions.CountPerKind(this.Context.ConnectionId, SubscriptionKind);

            return new RealtimeActionResult(
                false,
                ErrorCodes.ValidationFailed,
                count >= _options.Value.MaxSubscriptionsPerKind
                    ? $"单个连接最多订阅 {_options.Value.MaxSubscriptionsPerKind} 个容器的日志，请先退订其它容器。"
                    : "该容器的日志已在订阅中。");
        }

        subscription.Pump = this.StartPump(subscription, host.Name, containerId, startSeq, since);

        _logger.LogInformation(
            "订阅容器日志（连接={ConnectionId}，主机={HostId}，容器={ContainerId}，起始序号={StartSeq}）",
            this.Context.ConnectionId,
            host.Name,
            containerId,
            startSeq);

        return RealtimeActionResult.Ok;
    }

    /// <summary>
    /// 退订容器日志。
    /// </summary>
    /// <param name="containerId">容器标识。</param>
    /// <returns>退订结果。</returns>
    public async Task<RealtimeActionResult> UnsubscribeLogs(string containerId)
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
        // 连接断开 → 停掉该连接的全部推送任务。
        // 不做的话，泵任务会继续往一个已关闭的连接推消息，每秒产生一次异常。
        await _subscriptions.StopAllAsync(this.Context.ConnectionId).ConfigureAwait(false);

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动后台推送任务。
    /// </summary>
    /// <param name="subscription">订阅。</param>
    /// <param name="hostId">主机标识。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="startSeq">起始行序号。</param>
    /// <param name="since">起始时刻。</param>
    /// <returns>推送任务。</returns>
    private Task StartPump(
        RealtimeSubscription subscription,
        string hostId,
        string containerId,
        int startSeq,
        long? since)
    {
        var connectionId = this.Context.ConnectionId;
        var batchSize = _options.Value.LogBatchLines;
        var flushInterval = TimeSpan.FromMilliseconds(_options.Value.LogFlushIntervalMs);
        var logger = _logger;

        // 捕获到局部变量：后台任务不再依赖 Hub 实例（方法返回后它就不再被使用了）。
        var hubContext = _hubContext;

        return Task.Run(async () =>
        {
            // 自己的作用域：Hub 实例在方法返回后就被释放了，
            // 注入进来的 Scoped 服务（主机访问器、权限判定）不能带进后台任务。
            using var scope = _scopeFactory.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IContainerLogProvider>();

            var batch = new List<ContainerLogLine>(batchSize);
            var clock = Stopwatch.StartNew();
            var lastFlush = clock.Elapsed;

            try
            {
                await foreach (var line in provider
                    .StreamLogsAsync(
                        hostId,
                        containerId,
                        new ContainerLogStreamOptions
                        {
                            StartSeq = startSeq,
                            Since = since,
                            IncludeStderr = true,
                        },
                        subscription.Cancellation.Token)
                    .ConfigureAwait(false))
                {
                    batch.Add(line);

                    var elapsed = clock.Elapsed;
                    var shouldFlush = batch.Count >= batchSize || elapsed - lastFlush >= flushInterval;

                    if (shouldFlush)
                    {
                        await SendBatchAsync(hubContext, connectionId, containerId, batch, logger).ConfigureAwait(false);
                        batch.Clear();
                        lastFlush = elapsed;
                    }
                }

                // 流正常结束（容器停止）→ 把最后一批发出去，别把尾巴留在缓冲里。
                if (batch.Count > 0)
                {
                    await SendBatchAsync(hubContext, connectionId, containerId, batch, logger).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 退订或断开：预期路径。
            }
            catch (BusinessException exception)
            {
                logger.LogWarning(
                    "容器日志流失败（连接={ConnectionId}，容器={ContainerId}，Code={Code}）：{Reason}",
                    connectionId,
                    containerId,
                    exception.Code,
                    exception.Message);

                await SendErrorAsync(
                    hubContext,
                    connectionId,
                    exception.Code,
                    exception.Message,
                    logger).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "容器日志流异常（连接={ConnectionId}，容器={ContainerId}）",
                    connectionId,
                    containerId);

                await SendErrorAsync(
                    hubContext,
                    connectionId,
                    ErrorCodes.DockerApiFailed,
                    $"读取容器日志失败：{exception.Message}",
                    logger).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// 推送一批日志行。
    /// </summary>
    /// <param name="hubContext">Hub 上下文。</param>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="batch">本批日志行（会被清空）。</param>
    /// <param name="logger">日志。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task SendBatchAsync(
        IHubContext<ContainerLogHub> hubContext,
        string connectionId,
        string containerId,
        List<ContainerLogLine> batch,
        ILogger logger)
    {
        try
        {
            var payload = new ContainerLogPayload(containerId, [.. batch]);

            await hubContext.Clients
                .Client(connectionId)
                .SendAsync("OnLog", payload)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 连接已关闭是最常见的情况（用户关了页面），不必当错误刷屏。
            logger.LogDebug(
                "推送日志批次失败（连接={ConnectionId}，容器={ContainerId}）：{Reason}",
                connectionId,
                containerId,
                exception.Message);
        }
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
        IHubContext<ContainerLogHub> hubContext,
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
