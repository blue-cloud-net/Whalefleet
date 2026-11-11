using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Notify;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 把一条告警投递到规则绑定的外部渠道（Webhook / 邮件）。
/// </summary>
/// <remarks>
/// <b>best-effort</b>：某个渠道失败只记 Warning，不影响其它渠道，也不影响规则评估。
/// 告警本身已经落进站内通知，外部渠道是附加动作。
/// </remarks>
public sealed class NotificationDeliveryService
{
    private readonly INotificationChannelRepository _channels;
    private readonly IWebhookSender _webhook;
    private readonly IEmailSender _email;
    private readonly ISecretProtector _protector;
    private readonly ILogger<NotificationDeliveryService> _logger;

    /// <summary>
    /// 初始化投递服务。
    /// </summary>
    public NotificationDeliveryService(
        INotificationChannelRepository channels,
        IWebhookSender webhook,
        IEmailSender email,
        ISecretProtector protector,
        ILogger<NotificationDeliveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _channels = channels;
        _webhook = webhook;
        _email = email;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>
    /// 投递到指定渠道（只投启用的）。
    /// </summary>
    /// <param name="channelIds">规则绑定的渠道主键。</param>
    /// <param name="message">消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DeliverAsync(
        IReadOnlyCollection<Guid> channelIds,
        AlertNotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        ArgumentNullException.ThrowIfNull(message);

        if (channelIds.Count == 0)
        {
            return;
        }

        var channels = await _channels.QueryByIdsAsync(channelIds, cancellationToken).ConfigureAwait(false);

        foreach (var channel in channels.Where(channel => channel.Enabled))
        {
            try
            {
                var plaintext = _protector.Unprotect(channel.ConfigJson);

                switch (channel.Kind)
                {
                    case NotificationChannelKind.Webhook:
                        await _webhook.SendAsync(
                            NotificationChannelConfigCodec.ParseWebhook(plaintext),
                            message,
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case NotificationChannelKind.Email:
                        await _email.SendAsync(
                            NotificationChannelConfigCodec.ParseEmail(plaintext).Recipients,
                            message,
                            cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        _logger.LogWarning(
                            "未知的通知渠道类型，已跳过（渠道={ChannelId}）",
                            channel.Id);
                        continue;
                }

                _logger.LogInformation(
                    "告警已投递到渠道 {ChannelName}（规则={RuleKind}）",
                    channel.Name,
                    AlertKindText.ToText(message.RuleKind));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "告警投递失败（渠道={ChannelId}，规则={RuleKind}）",
                    channel.Id,
                    AlertKindText.ToText(message.RuleKind));
            }
        }
    }
}
