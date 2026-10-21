using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// 全局通知 Hub（<b>不含</b>主机标识）。
/// </summary>
/// <remarks>
/// <para><b>为什么是全局的</b>：容器异常退出是「平台级事件」——
/// 用户登录后应当在任何页面都能收到「某台机器上的某个容器挂了」的通知，
/// 而不是必须先进入那台主机才能知道。用带 <c>hostId</c> 的路径会强制用户先选主机，
/// 而出事时他未必知道是哪台。</para>
/// <para><b>可见性怎么保证</b>：连接建立时登记「连接 → 用户」，
/// 推事件时逐个连接判「该用户对这台主机有没有 <c>host.read</c>」，
/// 无权的一律不推。详见 <see cref="RealtimeConnectionRegistry"/> 的说明。</para>
/// <para><b>同一个 Hub 还承载「镜像操作进度」</b>：拉取 / 构建的进度是
/// <b>按请求</b>的（一个令牌对应一次操作、一个客户端），
/// 放在全局 Hub 上最自然——它本来就不属于某台主机。</para>
/// </remarks>
[Authorize]
public sealed class NotificationHub : Hub
{
    /// <summary>
    /// 镜像操作进度的订阅类别名。
    /// </summary>
    public const string PullSubscriptionKind = "pull";

    /// <summary>
    /// 进度令牌的最小长度。
    /// </summary>
    /// <remarks>
    /// 令牌是客户端生成的随机串，<b>不是</b>访问令牌。
    /// 但它会出现在服务端日志与异常消息里，因此仍要求足够长（避免被猜到后 hijack 别人的进度）。
    /// </remarks>
    public const int MinProgressTokenLength = 32;

    private readonly RealtimeSubscriptionRegistry _subscriptions;
    private readonly RealtimeConnectionRegistry _connections;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<NotificationHub> _logger;

    /// <summary>
    /// 初始化通知 Hub。
    /// </summary>
    /// <param name="subscriptions">订阅注册表。</param>
    /// <param name="connections">连接注册表。</param>
    /// <param name="options">实时通道选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public NotificationHub(
        RealtimeSubscriptionRegistry subscriptions,
        RealtimeConnectionRegistry connections,
        IOptions<RealtimeOptions> options,
        ILogger<NotificationHub> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptions = subscriptions;
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        var userId = RealtimeHostResolver.ResolveUserId(this.Context);

        _connections.Add(this.Context.ConnectionId, userId);

        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Remove(this.Context.ConnectionId);

        await _subscriptions.StopAllAsync(this.Context.ConnectionId).ConfigureAwait(false);

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// 订阅某次镜像操作的进度。
    /// </summary>
    /// <param name="token">进度令牌（客户端生成的随机串，与发起操作时用的同一个）。</param>
    /// <returns>订阅结果；失败时带错误码与消息。</returns>
    /// <remarks>
    /// <b>为什么令牌由客户端生成</b>：服务端没法凭空知道「这次拉取对应哪条连接」——
    /// 拉取请求是一个独立的 HTTP 请求，与 Hub 连接没有天然关联。
    /// 客户端先生成令牌，订阅它，再把它带进拉取请求，三者由此串起来。
    /// </remarks>
    public Task<RealtimeActionResult> SubscribePull(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < MinProgressTokenLength)
        {
            return Task.FromResult(new RealtimeActionResult(
                false,
                ErrorCodes.ValidationFailed,
                $"进度令牌至少 {MinProgressTokenLength} 个字符。"));
        }

        if (!IsValidToken(token))
        {
            return Task.FromResult(new RealtimeActionResult(
                false,
                ErrorCodes.ValidationFailed,
                "进度令牌只能包含字母、数字与短横线。"));
        }

        var subscription = _subscriptions.TryRegister(
            this.Context.ConnectionId,
            PullSubscriptionKind,
            token,
            _options.Value.MaxSubscriptionsPerKind);

        if (subscription is null)
        {
            return Task.FromResult(new RealtimeActionResult(
                false,
                ErrorCodes.ValidationFailed,
                $"单个连接最多订阅 {_options.Value.MaxSubscriptionsPerKind} 个进度频道，或该频道已在订阅中。"));
        }

        _logger.LogDebug(
            "订阅镜像操作进度（连接={ConnectionId}）",
            this.Context.ConnectionId);

        return Task.FromResult(RealtimeActionResult.Ok);
    }

    /// <summary>
    /// 退订某次镜像操作的进度。
    /// </summary>
    /// <param name="token">进度令牌。</param>
    /// <returns>退订结果。</returns>
    public async Task<RealtimeActionResult> UnsubscribePull(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new RealtimeActionResult(false, ErrorCodes.ValidationFailed, "进度令牌不能为空。");
        }

        await _subscriptions
            .RemoveAndStopAsync(this.Context.ConnectionId, PullSubscriptionKind, token)
            .ConfigureAwait(false);

        return RealtimeActionResult.Ok;
    }

    /// <summary>
    /// 校验进度令牌的字符集。
    /// </summary>
    /// <param name="token">令牌。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 限定字符集是为了让令牌能安全地出现在日志与异常消息里
    /// （没有换行、没有控制字符，也便于肉眼比对两处是否一致）。
    /// </remarks>
    private static bool IsValidToken(string token)
    {
        foreach (var character in token)
        {
            var isAllowed = char.IsAsciiLetterOrDigit(character) || character == '-';

            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }
}
