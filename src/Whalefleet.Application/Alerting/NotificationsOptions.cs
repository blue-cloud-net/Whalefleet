namespace Whalefleet.Application.Alerting;

/// <summary>
/// 通知渠道选项（配置节 <c>Notifications</c>）。
/// </summary>
/// <remarks>
/// <para>邮件服务器是<b>全局</b>配置（一个部署通常只有一个 SMTP），
/// 渠道里只存「发给谁」；Webhook 的地址与鉴权令牌则属于每个渠道自己。</para>
/// <para><b>SMTP 密码不得进入日志与响应</b>：本选项只从配置读，不做任何回显。</para>
/// </remarks>
public sealed class NotificationsOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Notifications";

    /// <summary>
    /// Webhook 发送超时（秒）。
    /// </summary>
    public int WebhookTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// SMTP 服务器地址；为空表示邮件渠道未启用。
    /// </summary>
    public string? SmtpHost { get; set; }

    /// <summary>
    /// SMTP 端口。
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// 是否使用 STARTTLS（587 通常为 true，465 隐式 TLS 通常为 false）。
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>
    /// SMTP 用户名；为空表示匿名发送。
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP 密码。
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// 发件人地址。
    /// </summary>
    public string? FromAddress { get; set; }

    /// <summary>
    /// 发件人显示名。
    /// </summary>
    public string FromDisplayName { get; set; } = "Whalefleet";

    /// <summary>
    /// 配置是否合法。
    /// </summary>
    public bool IsValid() =>
        this.WebhookTimeoutSeconds > 0 &&
        this.SmtpPort is > 0 and <= 65_535;
}
