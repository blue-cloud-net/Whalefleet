using System.Globalization;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Auditing;

/// <summary>
/// 把请求转成仓储过滤条件（<b>纯函数</b>，便于单测）。
/// </summary>
/// <remarks>
/// <para><b>为什么要默认时间窗</b>：审计表是<b>只增不减</b>的，不设窗口的分页查询会随
/// 运行时间变慢；「查询全部」在界面上也从来不是一个有意义的默认值。
/// 7 天覆盖了绝大多数「昨天/前天发生了什么」的现场，又不至于把首屏拖慢。</para>
/// <para><b>为什么非法时间区间要报错而不是纠正</b>：把 <c>from &gt; to</c> 悄悄交换会让人
/// 以为筛的是另一段时间——静默纠正比报错更危险。</para>
/// </remarks>
public static class AuditLogFilterBuilder
{
    /// <summary>
    /// 默认时间窗（天）。
    /// </summary>
    public const int DefaultWindowDays = 7;

    /// <summary>
    /// 构造过滤条件。
    /// </summary>
    /// <param name="category">类别（必填）。</param>
    /// <param name="request">请求。</param>
    /// <param name="hostId">固定的主机作用域（主机侧「事件」页用）；为 <see langword="null"/> 表示不限制。</param>
    /// <param name="nowUtc">当前时间（UTC），用于补默认时间窗。</param>
    /// <returns>仓储可直接使用的过滤条件。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="BusinessException">
    /// 时间区间颠倒（<c>1000</c>）或结果取值不认识（<c>1000</c>）。
    /// </exception>
    public static AuditLogFilter Build(
        AuditCategory category,
        ListAuditLogsRequest request,
        Guid? hostId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Unspecified 一律按 UTC 解释：前端用 toISOString() 送过来带 Z，
        // 但手工 curl 时常常不带——把它当本地时间会让「最近 7 天」在跨时区下静默偏移。
        var to = AsUtc(request.To) ?? nowUtc;
        var from = AsUtc(request.From) ?? to.AddDays(-DefaultWindowDays);

        if (from > to)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                "起始时间不能晚于结束时间。");
        }

        return new AuditLogFilter
        {
            Category = category,
            From = from,
            To = to,
            ActorName = Normalize(request.Actor),
            Action = Normalize(request.Action),
            TargetType = Normalize(request.TargetType),
            Outcome = ParseOutcome(request.Outcome),
            HostId = hostId,
            Search = Normalize(request.Search),
        };
    }

    /// <summary>
    /// 解析结果取值。
    /// </summary>
    /// <param name="value">请求里的字符串（<c>success</c> / <c>denied</c> / <c>failure</c>）。</param>
    /// <returns>枚举值；省略或空串返回 <see langword="null"/>（不筛选）。</returns>
    /// <exception cref="BusinessException">取值不认识（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>刻意不忽略未知值</b>：忽略会让界面显示「筛了但结果没变」——
    /// 而那正是最容易让人以为「数据丢了」的形态。
    /// </remarks>
    public static AuditOutcome? ParseOutcome(string? value)
    {
        var trimmed = Normalize(value);

        if (trimmed is null)
        {
            return null;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "success" => AuditOutcome.Success,
            "denied" => AuditOutcome.Denied,
            "failure" => AuditOutcome.Failure,
            _ => throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"不认识的结果取值「{trimmed}」，可选 success / denied / failure。"),
        };
    }

    /// <summary>
    /// 把可能不带时区的时间按 UTC 解释。
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
    };

    /// <summary>
    /// 去空白；空串归一为 <see langword="null"/>（"不筛选"）。
    /// </summary>
    private static string? Normalize(string? value) =>
        String.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 结果枚举的对外文本（与请求取值一致）。
    /// </summary>
    /// <param name="outcome">结果。</param>
    /// <returns><c>success</c> / <c>denied</c> / <c>failure</c>。</returns>
    internal static string ToText(AuditOutcome outcome) =>
        outcome.ToString().ToLowerInvariant();

    /// <summary>
    /// 把时间格式化成导出文件名里可用的片段。
    /// </summary>
    /// <param name="value">时间。</param>
    /// <returns>形如 <c>20261030-1200</c> 的片段（UTC）。</returns>
    internal static string ToFileStamp(DateTime value) =>
        value.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
}
