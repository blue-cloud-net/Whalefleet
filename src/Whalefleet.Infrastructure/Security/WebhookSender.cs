using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions.Notify;
using Whalefleet.Application.Alerting;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// Webhook 通知发送器（基于 <see cref="IHttpClientFactory"/>）。
/// </summary>
/// <remarks>
/// <para><b>不手写 <c>new HttpClient()</c></b>：长时间运行的实例会因 DNS 变更而持续连到旧地址。</para>
/// <para><b>令牌不写日志</b>：Bearer 令牌只进请求头，异常消息里也不拼接它。</para>
/// </remarks>
internal sealed class WebhookSender : IWebhookSender
{
    private const string _clientName = "notify";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<NotificationsOptions> _options;
    private readonly ILogger<WebhookSender> _logger;

    /// <summary>
    /// 初始化发送器。
    /// </summary>
    public WebhookSender(
        IHttpClientFactory httpClientFactory,
        IOptions<NotificationsOptions> options,
        ILogger<WebhookSender> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        WebhookChannelConfig config,
        AlertNotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(message);

        var client = _httpClientFactory.CreateClient(_clientName);
        client.Timeout = TimeSpan.FromSeconds(_options.Value.WebhookTimeoutSeconds);

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
        {
            Content = JsonContent.Create(new
            {
                type = "whalefleet.alert",
                ruleKind = AlertKindText.ToText(message.RuleKind),
                severity = AlertSeverityText.ToText(message.Severity),
                host = message.HostName,
                container = message.ContainerName,
                messageKey = message.MessageKey,
                message = message.Message,
                metricValue = message.MetricValue,
                thresholdValue = message.ThresholdValue,
                occurredAt = message.OccurredAt,
            }),
        };

        if (!String.IsNullOrWhiteSpace(config.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.BearerToken);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Webhook 返回非成功状态码：{StatusCode}（目标主机={Host}）",
                (int)response.StatusCode,
                new Uri(config.Url).Host);

            throw new InvalidOperationException(
                $"Webhook 返回 {((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }
}
