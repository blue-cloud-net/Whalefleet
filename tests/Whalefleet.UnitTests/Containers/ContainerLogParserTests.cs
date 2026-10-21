using Whalefleet.Application.Containers;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 容器日志解析器（<see cref="ContainerLogParser"/>）的单元测试。
/// </summary>
/// <remarks>
/// <para>核心契约是<b>「SDK 逐行回调，回调内容不含换行符」</b>——这是实测结论，
/// 不是直觉。旧实现把回调内容直接拼接，于是所有日志挤成一行
/// （<c>"helloworld"</c>），而 mock 数据是手工写的带换行文本，因此一直没被发现。
/// 这组用例就是为了把「逐行」这件事钉死。</para>
/// </remarks>
public class ContainerLogParserTests
{
    /// <summary>
    /// 解析器（默认摘掉时间戳前缀）。
    /// </summary>
    private readonly ContainerLogParser _parser = new();

    [Fact]
    public void 一次回调_产出一行()
    {
        var lines = _parser.Append("hello");

        lines.Should().HaveCount(1);
        lines[0]!.Content.Should().Be("hello");
    }

    [Fact]
    public void 连续回调_各自独立成行()
    {
        // 这是最容易写错的地方：把两次回调拼起来会得到 "helloworld"。
        var first = _parser.Append("hello");
        var second = _parser.Append("world");

        first[0]!.Content.Should().Be("hello");
        second[0]!.Content.Should().Be("world");
    }

    [Fact]
    public void 空块_不产出行()
    {
        // 恒定请求 timestamps=1 时回调内容永远不是空串，
        // 因此「空块」只可能来自「没有数据」，当成一行会凭空多出空行。
        _parser.Append(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void 块内含换行_按行切开()
    {
        // 兜底：万一 SDK 将来改成按块回调，仍能正确切分。
        var lines = _parser.Append("a\nb\nc");

        lines.Should().HaveCount(3);
        lines[0]!.Content.Should().Be("a");
        lines[2]!.Content.Should().Be("c");
    }

    [Fact]
    public void 结尾的换行_不产生空行()
    {
        var lines = _parser.Append("a\n");

        lines.Should().HaveCount(1);
        lines[0]!.Content.Should().Be("a");
    }

    [Fact]
    public void 行尾的回车_被去掉()
    {
        var lines = _parser.Append("hello\r");

        lines[0]!.Content.Should().Be("hello");
    }

    [Fact]
    public void 摘掉RFC3339Nano时间戳前缀()
    {
        var lines = _parser.Append("2026-01-02T03:04:05.123456789Z hello");

        lines[0]!.Timestamp.Should().NotBeNull();
        lines[0]!.Timestamp!.Value.Year.Should().Be(2026);
        lines[0]!.Content.Should().Be("hello");
    }

    [Fact]
    public void 九位小数_仍能解析出时间戳()
    {
        // .NET 只能存 7 位小数；解析器必须截断而不是放弃整个时间戳。
        var lines = _parser.Append("2026-01-02T03:04:05.123456789Z hello");

        lines[0]!.Timestamp.Should().NotBeNull();
    }

    [Fact]
    public void 不是时间戳的行_整行当正文()
    {
        var lines = _parser.Append("plain text without timestamp");

        lines[0]!.Timestamp.Should().BeNull();
        lines[0]!.Content.Should().Be("plain text without timestamp");
    }

    [Fact]
    public void 正文以数字开头_不被误伤()
    {
        // 前缀解析失败时必须**整行保留**：把 "42 items" 截成 "items" 是数据丢失。
        var lines = _parser.Append("42 items processed");

        lines[0]!.Timestamp.Should().BeNull();
        lines[0]!.Content.Should().Be("42 items processed");
    }

    [Fact]
    public void 不摘前缀时_时间戳留在正文里()
    {
        var parser = new ContainerLogParser(stripTimestampPrefix: false);

        var lines = parser.Append("2026-01-02T03:04:05Z hello");

        lines[0]!.Timestamp.Should().BeNull();
        lines[0]!.Content.Should().Be("2026-01-02T03:04:05Z hello");
    }

    [Fact]
    public void 没有空格的行_整行当正文()
    {
        var lines = _parser.Append("nospace");

        lines[0]!.Timestamp.Should().BeNull();
        lines[0]!.Content.Should().Be("nospace");
    }
}
