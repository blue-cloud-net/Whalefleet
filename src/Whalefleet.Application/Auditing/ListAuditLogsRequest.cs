namespace Whalefleet.Application.Auditing;

/// <summary>
/// 审计日志的查询请求（两个页面共用）。
/// </summary>
/// <remarks>
/// <para><b>为什么结果写成字符串而不是枚举</b>：前端按字符串做映射与展示，
/// 传 <c>outcome=success</c> 比 <c>outcome=0</c> 可读得多，也不会因为枚举顺序变化而错位。
/// 解析失败按 <c>1000</c> 报错（而不是忽略该条件）——静默忽略会让人以为「筛了就这些」。</para>
/// </remarks>
public sealed record ListAuditLogsRequest
{
    /// <summary>起始时刻（含）。省略时取结束时刻往前 7 天。</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时刻（含）。省略时取当前时间。</summary>
    public DateTime? To { get; init; }

    /// <summary>操作者名关键字（模糊、不区分大小写）。</summary>
    public string? Actor { get; init; }

    /// <summary>动作标识（精确匹配，如 <c>container.start</c>）。</summary>
    public string? Action { get; init; }

    /// <summary>目标类型（精确匹配，如 <c>container</c>）。</summary>
    public string? TargetType { get; init; }

    /// <summary>结果：<c>success</c> / <c>denied</c> / <c>failure</c>；省略表示全部。</summary>
    public string? Outcome { get; init; }

    /// <summary>自由文本关键字（目标名 / 主机名 / 说明）。</summary>
    public string? Search { get; init; }

    /// <summary>页码，从 0 开始。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数；后端上限 100，超出会被夹紧并回显实际值。</summary>
    public int? PageSize { get; init; }
}
