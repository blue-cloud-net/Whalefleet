using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Abstractions.Notify;
using Whalefleet.Application.Common;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 通知渠道用例。
/// </summary>
public sealed class NotificationChannelService
{
    private readonly INotificationChannelRepository _channels;
    private readonly ISecretProtector _protector;
    private readonly IWebhookSender _webhook;
    private readonly IEmailSender _email;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationChannelService> _logger;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    public NotificationChannelService(
        INotificationChannelRepository channels,
        ISecretProtector protector,
        IWebhookSender webhook,
        IEmailSender email,
        IAuditLogger audit,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILogger<NotificationChannelService> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _channels = channels;
        _protector = protector;
        _webhook = webhook;
        _email = email;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// 列出全部渠道（**不回传令牌 / 密码**）。
    /// </summary>
    public async Task<IReadOnlyList<NotificationChannelDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var channels = await _channels.QueryAllListAsync(cancellationToken).ConfigureAwait(false);

        return [.. channels.Select(ToDto)];
    }

    /// <summary>
    /// 新建渠道。
    /// </summary>
    public async Task<NotificationChannelDto> CreateAsync(
        SaveNotificationChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _channels.ExistsByNameAsync(request.Name, excludeId: null, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.NotificationChannelNameAlreadyExists,
                "通知渠道名已被占用");
        }

        var protectedConfig = this.EncodeConfig(request, existing: null);

        NotificationChannel channel;

        try
        {
            channel = NotificationChannel.Create(
                Guid.NewGuid(),
                request.Name,
                request.Kind,
                request.Enabled,
                protectedConfig);
        }
        catch (ArgumentException exception)
        {
            throw new BusinessException(ErrorCodes.ValidationFailed, exception.Message, exception);
        }

        await _channels.AddAsync(channel, cancellationToken).ConfigureAwait(false);
        await this.RecordAuditAsync(channel, "创建", cancellationToken).ConfigureAwait(false);

        return ToDto(channel);
    }

    /// <summary>
    /// 更新渠道。
    /// </summary>
    public async Task<NotificationChannelDto> UpdateAsync(
        SaveNotificationChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Id is not { } id)
        {
            throw new BusinessException(ErrorCodes.ValidationFailed, "更新渠道时必须提供 id");
        }

        var channel = await _channels.QueryByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.NotificationChannelNotFound, "通知渠道不存在");

        if (await _channels.ExistsByNameAsync(request.Name, id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.NotificationChannelNameAlreadyExists,
                "通知渠道名已被占用");
        }

        var protectedConfig = this.EncodeConfig(request, existing: channel);

        try
        {
            channel.Update(request.Name, request.Kind, request.Enabled, protectedConfig);
        }
        catch (ArgumentException exception)
        {
            throw new BusinessException(ErrorCodes.ValidationFailed, exception.Message, exception);
        }

        if (!await _channels.UpdateAsync(channel, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.NotificationChannelNotFound, "通知渠道不存在");
        }

        await this.RecordAuditAsync(channel, "更新", cancellationToken).ConfigureAwait(false);

        return ToDto(channel);
    }

    /// <summary>
    /// 删除渠道（软删除）。
    /// </summary>
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var channel = await _channels.QueryByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.NotificationChannelNotFound, "通知渠道不存在");

        if (!await _channels.RemoveByIdAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.NotificationChannelNotFound, "通知渠道不存在");
        }

        await this.RecordAuditAsync(channel, "删除", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送一条测试消息。
    /// </summary>
    /// <exception cref="BusinessException">渠道不存在或发送失败（<c>1703</c>）。</exception>
    public async Task TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var channel = await _channels.QueryByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.NotificationChannelNotFound, "通知渠道不存在");

        var message = new AlertNotificationMessage(
            AlertKind.HostOffline,
            AlertSeverity.Info,
            HostName: null,
            ContainerName: null,
            MessageKey: "notification.channel.test",
            Message: "这是一条 Whalefleet 通知渠道测试消息。",
            MetricValue: null,
            ThresholdValue: null,
            _clock.GetUtcNow().UtcDateTime);

        try
        {
            await this.SendAsync(channel, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "通知渠道测试失败（渠道={ChannelId}）", channel.Id);

            throw new BusinessException(
                ErrorCodes.NotificationChannelTestFailed,
                $"通知渠道测试失败：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 按渠道类型发送。
    /// </summary>
    internal async Task SendAsync(
        NotificationChannel channel,
        AlertNotificationMessage message,
        CancellationToken cancellationToken)
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
                throw new BusinessException(
                    ErrorCodes.ValidationFailed,
                    $"未知的通知渠道类型：{NotificationChannelKindText.ToText(channel.Kind)}");
        }
    }

    /// <summary>
    /// 编码并加密配置。
    /// </summary>
    private string EncodeConfig(
        SaveNotificationChannelRequest request,
        NotificationChannel? existing)
    {
        string plaintext;

        if (request.Kind == NotificationChannelKind.Webhook)
        {
            var token = request.WebhookBearerToken;

            // 更新时省略令牌 = 保留原令牌（界面不回显口令）
            if (token is null && existing is { Kind: NotificationChannelKind.Webhook })
            {
                try
                {
                    token = NotificationChannelConfigCodec
                        .ParseWebhook(_protector.Unprotect(existing.ConfigJson))
                        .BearerToken;
                }
                catch (Exception exception) when (exception is FormatException or BusinessException)
                {
                    // 原配置损坏：当作没有令牌处理，让本次保存把配置修正过来
                    _logger.LogWarning(exception, "读取既有 Webhook 配置失败（渠道={ChannelId}）", existing.Id);
                }
            }

            plaintext = NotificationChannelConfigCodec.Serialize(
                new WebhookChannelConfig(request.WebhookUrl ?? String.Empty, Normalize(token)));

            // 提前校验一次，避免把非法地址存进库
            _ = NotificationChannelConfigCodec.ParseWebhook(plaintext);
        }
        else
        {
            plaintext = NotificationChannelConfigCodec.Serialize(
                new EmailChannelConfig(request.EmailRecipients ?? []));

            _ = NotificationChannelConfigCodec.ParseEmail(plaintext);
        }

        return _protector.Protect(plaintext);
    }

    /// <summary>
    /// 实体 → DTO（只回传脱敏后的地址与收件人）。
    /// </summary>
    private NotificationChannelDto ToDto(NotificationChannel channel)
    {
        try
        {
            var plaintext = _protector.Unprotect(channel.ConfigJson);

            if (channel.Kind == NotificationChannelKind.Webhook)
            {
                var config = NotificationChannelConfigCodec.ParseWebhook(plaintext);

                return new NotificationChannelDto(
                    channel.Id,
                    channel.Name,
                    NotificationChannelKindText.ToText(channel.Kind),
                    channel.Enabled,
                    MaskUrl(config.Url),
                    !String.IsNullOrWhiteSpace(config.BearerToken),
                    null);
            }

            var email = NotificationChannelConfigCodec.ParseEmail(plaintext);

            return new NotificationChannelDto(
                channel.Id,
                channel.Name,
                NotificationChannelKindText.ToText(channel.Kind),
                channel.Enabled,
                null,
                false,
                email.Recipients);
        }
        catch (Exception exception) when (exception is FormatException or BusinessException)
        {
            _logger.LogWarning(exception, "通知渠道配置无法解析（渠道={ChannelId}）", channel.Id);

            return new NotificationChannelDto(
                channel.Id,
                channel.Name,
                NotificationChannelKindText.ToText(channel.Kind),
                channel.Enabled,
                null,
                false,
                null);
        }
    }

    /// <summary>
    /// 脱敏 Webhook 地址：去掉 userinfo 与查询串。
    /// </summary>
    private static string MaskUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "（地址无法解析）";
        }

        var port = uri.IsDefaultPort ? String.Empty : $":{uri.Port}";

        return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
    }

    /// <summary>
    /// 空白归一为 <see langword="null"/>。
    /// </summary>
    private static string? Normalize(string? value) =>
        String.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 写配置变更审计（best-effort）。
    /// </summary>
    private Task RecordAuditAsync(
        NotificationChannel channel,
        string operation,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.NotificationChannelUpdate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.NotificationChannel,
                TargetId = channel.Id.ToString("D"),
                TargetName = channel.Name,
                Detail = $"{operation}通知渠道（{NotificationChannelKindText.ToText(channel.Kind)}）",
            },
            cancellationToken);
}
