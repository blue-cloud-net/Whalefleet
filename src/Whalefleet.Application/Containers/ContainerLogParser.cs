using System.Globalization;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器日志行（解析后的形式，<b>不含</b>行号）。
/// </summary>
/// <param name="Timestamp">该行的时间戳；守护进程未提供或无法解析时为 <see langword="null"/>。</param>
/// <param name="Content">日志正文（已去掉时间戳前缀与行尾的 <c>\r</c>）。</param>
public sealed record ParsedLogLine(DateTimeOffset? Timestamp, string Content);

/// <summary>
/// 把 Docker 的日志流切成行，并摘出行首的时间戳前缀。
/// </summary>
/// <remarks>
/// <para><b>SDK 的实际契约（实测，勿凭直觉改）</b>：
/// <c>GetContainerLogsAsync</c> 的 <see cref="IProgress{T}"/> 回调是<b>逐行</b>触发的，
/// 每次给的是<b>一整行且不含行尾换行符</b>。也就是说
/// 「<c>hello</c> / <c>world</c>」这两行会分别回调两次，内容是
/// <c>"hello"</c> 与 <c>"world"</c>，<b>没有 <c>\n</c></b>。</para>
/// <para><b>由此推出的一条硬约束</b>：把回调内容直接拼接起来会得到
/// <c>"helloworld"</c>——所有日志挤成一行。<b>本项目的旧实现正是如此</b>：
/// 它把回调内容追加进一个 <see cref="System.Text.StringBuilder"/>，
/// 于是页面上看不到任何行分隔。这个缺陷只有在<b>真机</b>上才暴露
/// （mock 数据是手工写的带换行的文本），因此一直没被发现。
/// 0.5.0 改成结构化行数组后，本类就是那个「把行切开」的地方。</para>
/// <para><b>为什么还要处理块内含 <c>\n</c> 的情况</b>：SDK 逐行回调是<b>当前</b>的契约，
/// 而本类不假定它永远不变。万一将来改成按块回调，本类仍能把块正确切成行
/// （以 <c>\n</c> 为准），不会把整块当成一行。</para>
/// <para><b>为什么空块不产出行</b>：我们恒定请求 <c>timestamps=1</c>，
/// 于是<b>空行也会带上时间戳前缀</b>（形如 <c>"2026-01-01T00:00:00Z "</c>），
/// 回调内容永远不是空串。因此「空块」只可能来自「没有数据」，
/// 把它当成一行会凭空多出空行。</para>
/// <para><b>线程安全</b>：本类<b>不是</b>线程安全的。调用方是单条读取循环
/// （SDK 在一条读取循环里顺序回调），因此不需要加锁。</para>
/// </remarks>
public sealed class ContainerLogParser
{
    /// <summary>
    /// 是否从行首摘掉时间戳前缀。
    /// </summary>
    private readonly bool _stripTimestampPrefix;

    /// <summary>
    /// 初始化解析器。
    /// </summary>
    /// <param name="stripTimestampPrefix">
    /// 是否从行首摘掉时间戳前缀。为 <see langword="true"/> 时请求侧必须已经向守护进程
    /// 要了 <c>timestamps=1</c>，否则会把正文里恰好以「时间戳 空格」开头的行误伤。
    /// </param>
    public ContainerLogParser(bool stripTimestampPrefix = true)
    {
        _stripTimestampPrefix = stripTimestampPrefix;
    }

    /// <summary>
    /// 喂入一段内容，产出其中的行。
    /// </summary>
    /// <param name="chunk">一段内容（按 SDK 契约是一整行；也容忍块内含换行符）。</param>
    /// <returns>本次产出的行。</returns>
    public IReadOnlyList<ParsedLogLine> Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return [];
        }

        if (!chunk.Contains('\n', StringComparison.Ordinal))
        {
            // SDK 的逐行契约：整块就是一行。
            return [this.Parse(chunk)];
        }

        // 兜底：块里含换行符 → 按换行符切开。
        // 末尾的换行符不产生空行（"a\n" 是一行，不是两行）。
        var parts = chunk.Split('\n');
        var lines = new List<ParsedLogLine>(parts.Length);

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];

            if (index == parts.Length - 1 && part.Length == 0)
            {
                // 末尾的空段来自结尾的换行符，不是空行。
                break;
            }

            lines.Add(this.Parse(part));
        }

        return lines;
    }

    /// <summary>
    /// 解析一行：摘掉时间戳前缀（若开启）并去掉行尾的 <c>\r</c>。
    /// </summary>
    /// <param name="raw">原始行。</param>
    /// <returns>解析后的行。</returns>
    private ParsedLogLine Parse(string raw)
    {
        // CRLF：容器里的程序可能写 \r\n。Docker 自己的日志是 \n，
        // 但 \r 留在正文里会在页面上表现为「行尾有个奇怪的方块」。
        if (raw.Length > 0 && raw[^1] == '\r')
        {
            raw = raw[..^1];
        }

        if (!_stripTimestampPrefix)
        {
            return new ParsedLogLine(null, raw);
        }

        var separator = raw.IndexOf(' ', StringComparison.Ordinal);

        if (separator <= 0)
        {
            // 没有空格：不可能是「时间戳 空格 正文」，整行当正文。
            return new ParsedLogLine(null, raw);
        }

        if (!TryParseTimestamp(raw.AsSpan(0, separator), out var timestamp))
        {
            return new ParsedLogLine(null, raw);
        }

        return new ParsedLogLine(timestamp, raw[(separator + 1)..]);
    }

    /// <summary>
    /// 解析 Docker 的时间戳前缀。
    /// </summary>
    /// <param name="prefix">前缀文本。</param>
    /// <param name="timestamp">解析出的时刻。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>Docker 用 RFC3339Nano，<b>纳秒</b>精度（9 位小数），
    /// 而 .NET 的 <see cref="DateTime"/> 只能存 100 纳秒（7 位小数）。
    /// 多数情况下 .NET 会自动截断，但这里显式做一次兜底：
    /// 万一解析器直接拒绝超长小数，就手动截到 7 位再试一次——
    /// <b>丢掉纳秒精度可以，丢掉整个时间戳不行</b>（那行就变成「没有时间戳的行」了）。</para>
    /// </remarks>
    private static bool TryParseTimestamp(ReadOnlySpan<char> prefix, out DateTimeOffset timestamp)
    {
        const DateTimeStyles styles =
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

        if (DateTimeOffset.TryParse(prefix, CultureInfo.InvariantCulture, styles, out timestamp))
        {
            return true;
        }

        var dot = prefix.IndexOf('.');

        if (dot < 0)
        {
            return false;
        }

        // 小数点之后只留 7 位（.NET 的精度上限），其余丢掉。
        var fractionStart = dot + 1;
        var keep = Math.Min(7, prefix.Length - fractionStart);

        if (keep <= 0)
        {
            return false;
        }

        var truncated = prefix[..(fractionStart + keep)];

        return DateTimeOffset.TryParse(truncated, CultureInfo.InvariantCulture, styles, out timestamp);
    }
}
