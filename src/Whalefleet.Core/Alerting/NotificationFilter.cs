namespace Whalefleet.Core.Alerting;

/// <summary>
/// 站内通知的查询条件。
/// </summary>
/// <remarks>
/// <para><b>可见性不是可选筛选项，而是硬条件</b>：非管理员只能看到
/// <c>host_id IS NULL</c>（全局）或自己可见主机的通知。</para>
/// <para><b>为什么区分「不限制主机」与「一台主机都不可见」</b>：两者的 SQL 完全不同
/// （前者不加条件，后者只能看全局通知）。用空集合同时表达两者会让非管理员看到全部。</para>
/// </remarks>
public sealed record NotificationFilter
{
    /// <summary>
    /// 是否不限制主机（管理员）。
    /// </summary>
    public bool IncludeAllHosts { get; init; }

    /// <summary>
    /// 可见主机主键集合（<see cref="IncludeAllHosts"/> 为 <see langword="false"/> 时生效）。
    /// </summary>
    public IReadOnlyCollection<Guid> VisibleHostIds { get; init; } = [];

    /// <summary>
    /// 只看未读；<see langword="null"/> 表示不限。
    /// </summary>
    public bool? UnreadOnly { get; init; }

    /// <summary>
    /// 严重度筛选；<see langword="null"/> 表示不限。
    /// </summary>
    public AlertSeverity? Severity { get; init; }

    /// <summary>
    /// 来源类型筛选；<see langword="null"/> 表示不限。
    /// </summary>
    public NotificationSourceKind? SourceKind { get; init; }

    /// <summary>
    /// 关键字（匹配消息 key / 主机名 / 参数 JSON，模糊、不区分大小写）。
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// 起始时刻（含，UTC）。
    /// </summary>
    public DateTime? From { get; init; }

    /// <summary>
    /// 结束时刻（含，UTC）。
    /// </summary>
    public DateTime? To { get; init; }
}
