namespace Whalefleet.Core.Auditing;

/// <summary>
/// 审计日志的查询条件。
/// </summary>
/// <remarks>
/// <para><b>为什么是一个对象而不是十个参数</b>：条件之间是「可选叠加」关系，
/// 位置参数会让调用点出现一串 <c>null</c>，而错位一个 <c>null</c> 不会报错，
/// 只会让筛选悄悄少一个维度。</para>
/// <para><b>为什么放在 Core</b>：它是仓储接口的入参，而仓储接口在 Core——
/// 参数类型不能比接口更「外层」。</para>
/// </remarks>
public sealed record AuditLogFilter
{
    /// <summary>类别（必填：两个页面各查自己的）。</summary>
    public required AuditCategory Category { get; init; }

    /// <summary>起始时刻（含，UTC）。</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时刻（含，UTC）。</summary>
    public DateTime? To { get; init; }

    /// <summary>操作者名关键字（模糊、不区分大小写）。</summary>
    public string? ActorName { get; init; }

    /// <summary>动作标识（精确匹配）。</summary>
    public string? Action { get; init; }

    /// <summary>目标类型（精确匹配）。</summary>
    public string? TargetType { get; init; }

    /// <summary>结果（精确匹配）。</summary>
    public AuditOutcome? Outcome { get; init; }

    /// <summary>作用域主机主键（主机侧「事件」页固定带上）。</summary>
    public Guid? HostId { get; init; }

    /// <summary>自由文本关键字：同时匹配目标名 / 主机名 / 附加说明（模糊、不区分大小写）。</summary>
    public string? Search { get; init; }
}
