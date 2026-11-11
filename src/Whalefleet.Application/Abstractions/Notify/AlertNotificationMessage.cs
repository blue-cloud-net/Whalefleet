using Whalefleet.Core.Alerting;

namespace Whalefleet.Application.Abstractions.Notify;

/// <summary>
/// 发给外部渠道的告警消息（Webhook / 邮件共用）。
/// </summary>
/// <param name="RuleKind">规则类型。</param>
/// <param name="Severity">严重度。</param>
/// <param name="HostName">主机名；全局事件为 <see langword="null"/>。</param>
/// <param name="ContainerName">容器名；非容器事件为 <see langword="null"/>。</param>
/// <param name="MessageKey">i18n key。</param>
/// <param name="Message">服务端渲染的纯文本说明（外部渠道没有 i18n 运行时）。</param>
/// <param name="MetricValue">触发时的实际值；事件型规则为 <see langword="null"/>。</param>
/// <param name="ThresholdValue">命中的阈值；事件型规则为 <see langword="null"/>。</param>
/// <param name="OccurredAt">触发时刻（UTC）。</param>
public sealed record AlertNotificationMessage(
    AlertKind RuleKind,
    AlertSeverity Severity,
    string? HostName,
    string? ContainerName,
    string MessageKey,
    string Message,
    double? MetricValue,
    double? ThresholdValue,
    DateTime OccurredAt);
