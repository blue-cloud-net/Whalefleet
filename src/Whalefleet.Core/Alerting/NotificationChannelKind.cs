namespace Whalefleet.Core.Alerting;

/// <summary>
/// 通知渠道类型。
/// </summary>
public enum NotificationChannelKind
{
    /// <summary>
    /// HTTP Webhook（零新依赖，用 <c>IHttpClientFactory</c> 发送）。
    /// </summary>
    Webhook,

    /// <summary>
    /// 邮件（SMTP）。
    /// </summary>
    Email,
}
