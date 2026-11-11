namespace Whalefleet.Application.Abstractions.Notify;

/// <summary>
/// Webhook 渠道的配置（加密前的明文形态）。
/// </summary>
/// <param name="Url">目标地址。</param>
/// <param name="BearerToken">可选的 Bearer 令牌；为空表示不带鉴权头。</param>
public sealed record WebhookChannelConfig(string Url, string? BearerToken);

/// <summary>
/// 邮件渠道的配置（加密前的明文形态）。
/// </summary>
/// <param name="Recipients">收件人地址列表。</param>
public sealed record EmailChannelConfig(IReadOnlyList<string> Recipients);

/// <summary>
/// Webhook 发送器（外部资源抽象）。
/// </summary>
public interface IWebhookSender
{
    /// <summary>
    /// 发送一条告警。
    /// </summary>
    /// <param name="config">渠道配置。</param>
    /// <param name="message">消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="Exception">网络失败或非 2xx 响应（由调用方统一降级处理）。</exception>
    Task SendAsync(
        WebhookChannelConfig config,
        AlertNotificationMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 邮件发送器（外部资源抽象）。
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// 发送一条告警邮件。
    /// </summary>
    /// <param name="recipients">收件人。</param>
    /// <param name="message">消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="InvalidOperationException">SMTP 未配置。</exception>
    /// <exception cref="Exception">发送失败（由调用方统一降级处理）。</exception>
    Task SendAsync(
        IReadOnlyList<string> recipients,
        AlertNotificationMessage message,
        CancellationToken cancellationToken = default);
}
