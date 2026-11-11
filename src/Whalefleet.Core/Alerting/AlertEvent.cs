using Whalefleet.Core.Common;

namespace Whalefleet.Core.Alerting;

/// <summary>
/// 一条告警触发事实（只追加）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独建表而不只写通知</b>：冷却期需要「上一次触发是什么时候」，
/// 而通知可能因渠道发送失败 / 用户删除而不可靠。事件表是规则引擎自己的事实记录，
/// 只增不删，用来回答「这条规则最近有没有报过」。</para>
/// <para><b>名字是快照</b>：主机与容器可能随后被删除或改名，事件里的名字保留触发当时的文本。
/// </para>
/// </remarks>
public sealed class AlertEvent : Entity<Guid>
{
    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Record"/>。
    /// </summary>
    private AlertEvent()
    {
    }

    /// <summary>
    /// 触发的规则主键。
    /// </summary>
    public Guid RuleId { get; private set; }

    /// <summary>
    /// 规则类型（快照：规则被删后仍能解释这条事件）。
    /// </summary>
    public AlertKind RuleKind { get; private set; }

    /// <summary>
    /// 目标键（冷却判定的作用域）：<c>host:名称</c> / <c>host:名称:container:ID</c> / <c>global</c>。
    /// </summary>
    public string TargetKey { get; private set; } = String.Empty;

    /// <summary>
    /// 主机主键（<c>hosts.id</c>）；全局事件为 <see langword="null"/>。
    /// </summary>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// 主机名快照。
    /// </summary>
    public string? HostName { get; private set; }

    /// <summary>
    /// 容器标识；非容器事件为 <see langword="null"/>。
    /// </summary>
    public string? ContainerId { get; private set; }

    /// <summary>
    /// 容器名快照。
    /// </summary>
    public string? ContainerName { get; private set; }

    /// <summary>
    /// 本次触发的严重度。
    /// </summary>
    public AlertSeverity Severity { get; private set; }

    /// <summary>
    /// 触发时的实际值（百分比 / 字节每秒 / 次数）；事件型规则为 <see langword="null"/>。
    /// </summary>
    public double? MetricValue { get; private set; }

    /// <summary>
    /// 命中的阈值；事件型规则为 <see langword="null"/>。
    /// </summary>
    public double? ThresholdValue { get; private set; }

    /// <summary>
    /// 触发时刻（UTC）。
    /// </summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>
    /// 记录一条告警事件。
    /// </summary>
    public static AlertEvent Record(
        Guid id,
        Guid ruleId,
        AlertKind ruleKind,
        string targetKey,
        Guid? hostId,
        string? hostName,
        string? containerId,
        string? containerName,
        AlertSeverity severity,
        double? metricValue,
        double? thresholdValue,
        DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        if (occurredAt.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("触发时刻必须是 UTC", nameof(occurredAt));
        }

        return new AlertEvent
        {
            Id = id,
            RuleId = ruleId,
            RuleKind = ruleKind,
            TargetKey = targetKey.Trim(),
            HostId = hostId,
            HostName = hostName,
            ContainerId = containerId,
            ContainerName = containerName,
            Severity = severity,
            MetricValue = metricValue,
            ThresholdValue = thresholdValue,
            OccurredAt = occurredAt,
        };
    }
}
