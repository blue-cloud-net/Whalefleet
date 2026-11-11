namespace Whalefleet.Application.Alerting;

/// <summary>
/// 告警规则的对外投影。
/// </summary>
public sealed record AlertRuleDto(
    Guid Id,
    Guid? HostId,
    string? HostName,
    string Kind,
    bool Enabled,
    double? InfoThreshold,
    double? WarningThreshold,
    double? CriticalThreshold,
    string Severity,
    int ConsecutiveSamples,
    int? WindowSeconds,
    int CooldownSeconds,
    IReadOnlyList<Guid> ChannelIds,
    DateTime CreatedAt);

/// <summary>
/// 新建告警规则的请求。
/// </summary>
/// <param name="Host">主机名；空表示全局规则。**不叫 hostId**：请求体里没有主机主键。</param>
public sealed record CreateAlertRuleRequest(
    string? Host,
    string Kind,
    bool Enabled,
    double? InfoThreshold,
    double? WarningThreshold,
    double? CriticalThreshold,
    Core.Alerting.AlertSeverity Severity,
    int ConsecutiveSamples,
    int? WindowSeconds,
    int CooldownSeconds,
    IReadOnlyList<Guid>? ChannelIds);

/// <summary>
/// 更新告警规则的请求。
/// </summary>
public sealed record UpdateAlertRuleRequest(
    Guid Id,
    string? Host,
    string Kind,
    bool Enabled,
    double? InfoThreshold,
    double? WarningThreshold,
    double? CriticalThreshold,
    Core.Alerting.AlertSeverity Severity,
    int ConsecutiveSamples,
    int? WindowSeconds,
    int CooldownSeconds,
    IReadOnlyList<Guid>? ChannelIds);

/// <summary>
/// 启用 / 停用规则的请求。
/// </summary>
public sealed record SetAlertRuleEnabledRequest(Guid Id, bool Enabled);

/// <summary>
/// 按主键删除告警规则的请求。
/// </summary>
public sealed record RemoveAlertRuleRequest(Guid Id);

/// <summary>
/// 按主键删除通知渠道的请求。
/// </summary>
public sealed record RemoveNotificationChannelRequest(Guid Id);

/// <summary>
/// 按主键测试通知渠道的请求。
/// </summary>
public sealed record TestNotificationChannelRequest(Guid Id);

/// <summary>
/// 通知渠道的对外投影（**不含任何口令 / 令牌**）。
/// </summary>
public sealed record NotificationChannelDto(
    Guid Id,
    string Name,
    string Kind,
    bool Enabled,
    string? WebhookUrl,
    bool HasWebhookToken,
    IReadOnlyList<string>? EmailRecipients);

/// <summary>
/// 新建 / 更新通知渠道的请求。
/// </summary>
/// <param name="Id">更新时必填；新建为 <see langword="null"/>。</param>
/// <param name="WebhookBearerToken">更新时可省略，表示保留原令牌。</param>
public sealed record SaveNotificationChannelRequest(
    Guid? Id,
    string Name,
    Core.Alerting.NotificationChannelKind Kind,
    bool Enabled,
    string? WebhookUrl,
    string? WebhookBearerToken,
    IReadOnlyList<string>? EmailRecipients);
