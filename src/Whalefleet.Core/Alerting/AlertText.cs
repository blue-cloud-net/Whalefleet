namespace Whalefleet.Core.Alerting;

/// <summary>
/// <see cref="AlertSeverity"/> 的对外 / 存储文本形态。
/// </summary>
public static class AlertSeverityText
{
    /// <summary>
    /// 枚举 → 小写文本。
    /// </summary>
    public static string ToText(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => "info",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "未知的严重度"),
    };

    /// <summary>
    /// 文本 → 枚举（大小写与首尾空白不敏感）。
    /// </summary>
    public static AlertSeverity Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return text.Trim().ToLowerInvariant() switch
        {
            "info" => AlertSeverity.Info,
            "warning" => AlertSeverity.Warning,
            "critical" => AlertSeverity.Critical,
            _ => throw new FormatException($"未知的严重度文本：{text}"),
        };
    }
}

/// <summary>
/// <see cref="NotificationChannelKind"/> 的对外 / 存储文本形态。
/// </summary>
public static class NotificationChannelKindText
{
    /// <summary>
    /// 枚举 → 小写文本。
    /// </summary>
    public static string ToText(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Webhook => "webhook",
        NotificationChannelKind.Email => "email",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的通知渠道类型"),
    };

    /// <summary>
    /// 文本 → 枚举（大小写与首尾空白不敏感）。
    /// </summary>
    public static NotificationChannelKind Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return text.Trim().ToLowerInvariant() switch
        {
            "webhook" => NotificationChannelKind.Webhook,
            "email" => NotificationChannelKind.Email,
            _ => throw new FormatException($"未知的通知渠道类型文本：{text}"),
        };
    }
}
