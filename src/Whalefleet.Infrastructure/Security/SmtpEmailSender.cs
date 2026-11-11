using System.Globalization;
using System.Net;
using System.Net.Mail;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions.Notify;
using Whalefleet.Application.Alerting;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 邮件通知发送器（BCL <see cref="SmtpClient"/>，零新依赖）。
/// </summary>
/// <remarks>
/// <para><b>SMTP 服务器是部署级配置</b>（<c>Notifications:*</c>），渠道里只存收件人。</para>
/// <para><b>密码永不写日志</b>：本类只在构造 <see cref="NetworkCredential"/> 时使用它。</para>
/// </remarks>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptions<NotificationsOptions> _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    /// <summary>
    /// 初始化发送器。
    /// </summary>
    public SmtpEmailSender(IOptions<NotificationsOptions> options, ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        IReadOnlyList<string> recipients,
        AlertNotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.Value;

        if (String.IsNullOrWhiteSpace(options.SmtpHost)
            || String.IsNullOrWhiteSpace(options.FromAddress))
        {
            throw new InvalidOperationException("SMTP 未配置（Notifications:SmtpHost / FromAddress 为空）");
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = BuildSubject(message),
            Body = BuildBody(message),
            IsBodyHtml = false,
        };

        foreach (var recipient in recipients)
        {
            mail.To.Add(new MailAddress(recipient));
        }

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.UseStartTls,
        };

        if (!String.IsNullOrWhiteSpace(options.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(options.SmtpUsername, options.SmtpPassword);
        }

        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "告警邮件已发送（规则={RuleKind}，收件人数量={Count}）",
            AlertKindText.ToText(message.RuleKind),
            recipients.Count);
    }

    /// <summary>
    /// 邮件主题。
    /// </summary>
    private static string BuildSubject(AlertNotificationMessage message)
    {
        var host = String.IsNullOrWhiteSpace(message.HostName) ? "-" : message.HostName;

        return $"[Whalefleet][{AlertSeverityText.ToText(message.Severity)}] "
            + $"{AlertKindText.ToText(message.RuleKind)} - {host}";
    }

    /// <summary>
    /// 邮件正文（纯文本）。
    /// </summary>
    private static string BuildBody(AlertNotificationMessage message)
    {
        var lines = new List<string>
        {
            "Whalefleet 告警",
            String.Empty,
            $"规则：{AlertKindText.ToText(message.RuleKind)}",
            $"严重度：{AlertSeverityText.ToText(message.Severity)}",
            $"主机：{message.HostName ?? "-"}",
            $"容器：{message.ContainerName ?? "-"}",
            $"说明：{message.Message}",
        };

        if (message.MetricValue is { } metric)
        {
            lines.Add($"实际值：{metric.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (message.ThresholdValue is { } threshold)
        {
            lines.Add($"阈值：{threshold.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        lines.Add($"时间（UTC）：{message.OccurredAt:yyyy-MM-dd HH:mm:ss}");

        return String.Join(Environment.NewLine, lines);
    }
}
