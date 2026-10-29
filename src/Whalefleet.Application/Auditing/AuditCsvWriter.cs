using System.Globalization;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Auditing;

/// <summary>
/// 审计日志的 CSV 序列化（<b>纯函数</b>，便于单测）。
/// </summary>
/// <remarks>
/// <para><b>为什么要自己写而不是引一个 CSV 库</b>：需要的规则只有三条
/// （逗号 / 引号 / 换行的转义、表头、日期格式），而库会顺带引入
/// 「要不要引号包一切」「要不要自动检测分隔符」这类与本次无关的分歧。</para>
/// <para><b>为什么要写 UTF-8 BOM</b>：Excel 打开不带 BOM 的 UTF-8 CSV 时按本地代码页解码，
/// 中文列头会变成乱码。这一条看似细枝末节，但它决定了「导出功能对使用者是否可用」。</para>
/// <para><b>为什么表头与页面列一致</b>：验收口径就是这两者相等。
/// 差一列的话，用户会以为页面上少显示了什么。</para>
/// </remarks>
public static class AuditCsvWriter
{
    /// <summary>
    /// UTF-8 BOM（<c>U+FEFF</c>）。写在文件最前面，Excel 据此判定编码。
    /// </summary>
    public const string Bom = "\uFEFF";

    /// <summary>
    /// 活动日志的表头。
    /// </summary>
    public static readonly string[] ActivityHeader =
    [
        "时间 (UTC)",
        "操作人",
        "动作",
        "目标类型",
        "目标",
        "主机",
        "结果",
        "错误码",
        "来源 IP",
        "说明",
    ];

    /// <summary>
    /// 认证日志的表头。
    /// </summary>
    /// <remarks>
    /// 比活动日志少了「目标类型 / 目标 / 主机」——认证日志没有资源目标，
    /// 留三列恒为空只会让人怀疑是不是导出坏了。
    /// </remarks>
    public static readonly string[] AuthenticationHeader =
    [
        "时间 (UTC)",
        "用户",
        "动作",
        "结果",
        "错误码",
        "来源 IP",
        "说明",
    ];

    /// <summary>
    /// 取某类审计的表头。
    /// </summary>
    /// <param name="category">类别。</param>
    /// <returns>表头列名（顺序即列顺序）。</returns>
    public static string[] HeaderFor(AuditCategory category) =>
        category == AuditCategory.Authentication ? AuthenticationHeader : ActivityHeader;

    /// <summary>
    /// 把一批审计记录写成 CSV。
    /// </summary>
    /// <param name="writer">目标写入器（通常是响应体）。</param>
    /// <param name="category">类别（决定表头与列数）。</param>
    /// <param name="logs">记录（按调用方给的顺序写出）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    /// <remarks>
    /// <b>行尾用 <c>\r\n</c></b>：Excel 与多数 Windows 工具对 LF 结尾的 CSV 兼容性更好的是 CRLF，
    /// 而 RFC 4180 本身也要求 CRLF。
    /// </remarks>
    public static async Task WriteAsync(
        TextWriter writer,
        AuditCategory category,
        IReadOnlyList<AuditLog> logs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logs);

        // 行尾强制 CRLF：RFC 4180 要求 CRLF，而 StreamWriter 默认用 Environment.NewLine
        // （Linux 上是 \n）。不确定的行尾会让「同一份导出在 Linux 与 Windows 上不一样」，
        // 而排查时几乎没人会去比对字节。
        var previousNewLine = writer.NewLine;
        writer.NewLine = "\r\n";

        try
        {
            await writer.WriteAsync(Bom.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer
                .WriteLineAsync(String.Join(',', HeaderFor(category).Select(Escape)))
                .ConfigureAwait(false);

            foreach (var log in logs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await writer.WriteLineAsync(BuildRow(category, log)).ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.NewLine = previousNewLine;
        }
    }

    /// <summary>
    /// 构造一行（不含行尾）。
    /// </summary>
    /// <param name="category">类别。</param>
    /// <param name="log">审计记录。</param>
    /// <returns>已转义的 CSV 行。</returns>
    public static string BuildRow(AuditCategory category, AuditLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var time = log.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var outcome = log.Outcome.ToString().ToLowerInvariant();
        var errorCode = log.ErrorCode?.ToString(CultureInfo.InvariantCulture) ?? String.Empty;

        var cells = category == AuditCategory.Authentication
            ? new[]
            {
                time,
                log.ActorName,
                log.Action,
                outcome,
                errorCode,
                log.ClientIp ?? String.Empty,
                log.Detail ?? String.Empty,
            }
            : new[]
            {
                time,
                log.ActorName,
                log.Action,
                log.TargetType ?? String.Empty,
                log.TargetName ?? String.Empty,
                log.HostName ?? String.Empty,
                outcome,
                errorCode,
                log.ClientIp ?? String.Empty,
                log.Detail ?? String.Empty,
            };

        return String.Join(',', cells.Select(Escape));
    }

    /// <summary>
    /// 按 RFC 4180 转义一个单元格。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>可安全嵌入 CSV 的文本。</returns>
    /// <remarks>
    /// 只在<b>需要时</b>加引号：给所有单元格都加引号虽然也不会错，但会让
    /// 「用文本编辑器看一眼」变得难受，且与多数工具的输出习惯不一致。
    /// </remarks>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var needsQuotes = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal);

        if (!needsQuotes)
        {
            return value;
        }

        // 引号内的引号要成对翻倍（RFC 4180）。
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
