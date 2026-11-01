using FluentAssertions;

using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;

using Xunit;

namespace Whalefleet.UnitTests.Stacks;

/// <summary>
/// <see cref="Shellwords"/> 的行为测试。
/// </summary>
/// <remarks>
/// 引号必须生效——否则 <c>command: sh -c "echo hello world"</c> 会被拆成
/// 四个独立的词，参数边界整个错位。
/// </remarks>
public class ShellwordsTests
{
    [Fact]
    public void 拆分_普通单词_按空白切开()
    {
        Shellwords.Split("nginx -g daemon").Should().BeEquivalentTo(["nginx", "-g", "daemon"]);
    }

    [Fact]
    public void 拆分_双引号内的空格属于同一个词()
    {
        Shellwords.Split("sh -c \"echo hello world\"")
            .Should().BeEquivalentTo(["sh", "-c", "echo hello world"]);
    }

    [Fact]
    public void 拆分_单引号内的内容按字面处理()
    {
        Shellwords.Split("sh -c 'echo $HOME'")
            .Should().BeEquivalentTo(["sh", "-c", "echo $HOME"]);
    }

    [Fact]
    public void 拆分_反斜杠转义下一个字符()
    {
        Shellwords.Split("echo a\\ b").Should().BeEquivalentTo(["echo", "a b"]);
    }

    [Fact]
    public void 拆分_双引号内的反斜杠可转义引号()
    {
        Shellwords.Split("\"a\\\"b\"").Should().BeEquivalentTo(["a\"b"]);
    }

    [Fact]
    public void 拆分_连续空白不产生空词()
    {
        Shellwords.Split("  a   b  ").Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void 拆分_空串_得到空列表()
    {
        Shellwords.Split("").Should().BeEmpty();
        Shellwords.Split("   ").Should().BeEmpty();
    }

    [Fact]
    public void 拆分_未闭合引号_报错()
    {
        var action = () => Shellwords.Split("sh -c \"echo");

        action.Should().Throw<BusinessException>();
    }
}
