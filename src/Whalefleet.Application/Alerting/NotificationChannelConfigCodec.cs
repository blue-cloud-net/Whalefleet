using System.Text.Json;

using Whalefleet.Application.Abstractions.Notify;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 通知渠道配置的序列化 / 反序列化（加密前的明文 JSON）。
/// </summary>
/// <remarks>
/// <para>配置在落库前由 <c>ISecretProtector</c> 加密，因此这里只管明文 JSON 的形状。</para>
/// <para><b>不使用源生成器 / 多态</b>：只有两种固定形状，显式解析比多态更可控。</para>
/// </remarks>
public static class NotificationChannelConfigCodec
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 序列化 Webhook 配置。
    /// </summary>
    public static string Serialize(WebhookChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return JsonSerializer.Serialize(config, _options);
    }

    /// <summary>
    /// 解析 Webhook 配置。
    /// </summary>
    /// <exception cref="FormatException">JSON 非法或缺少地址。</exception>
    public static WebhookChannelConfig ParseWebhook(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var config = JsonSerializer.Deserialize<WebhookChannelConfig>(json, _options)
            ?? throw new FormatException("Webhook 渠道配置为空");

        if (String.IsNullOrWhiteSpace(config.Url))
        {
            throw new FormatException("Webhook 渠道缺少地址");
        }

        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException("Webhook 地址必须是 http / https 绝对地址");
        }

        return config;
    }

    /// <summary>
    /// 序列化邮件配置。
    /// </summary>
    public static string Serialize(EmailChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return JsonSerializer.Serialize(config, _options);
    }

    /// <summary>
    /// 解析邮件配置。
    /// </summary>
    /// <exception cref="FormatException">JSON 非法或没有收件人。</exception>
    public static EmailChannelConfig ParseEmail(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var config = JsonSerializer.Deserialize<EmailChannelConfig>(json, _options)
            ?? throw new FormatException("邮件渠道配置为空");

        var recipients = (config.Recipients ?? [])
            .Where(address => !String.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (recipients.Length == 0)
        {
            throw new FormatException("邮件渠道至少需要一个收件人");
        }

        foreach (var address in recipients)
        {
            if (!address.Contains('@', StringComparison.Ordinal))
            {
                throw new FormatException($"收件人地址非法：{address}");
            }
        }

        return new EmailChannelConfig(recipients);
    }
}
