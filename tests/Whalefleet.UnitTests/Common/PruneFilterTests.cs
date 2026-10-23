using Whalefleet.Application.Common;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Common;

/// <summary>
/// 清理过滤器的标签归一化与构造。
/// </summary>
/// <remarks>
/// <para><b>为什么这层要有单元测试</b>：标签过滤是<b>唯一</b>能把清理范围收窄的手段——
/// 不带它，<c>volumes/prune</c> 会清掉本机全部未被引用的卷。
/// 而「静默不生效」的失败形态是「过滤没传出去」，
/// 那只能靠集成测试观察到「别人的卷被删了」。这里守住的是<b>入参形状</b>：
/// 空值要归一成「不过滤」、控制字符要当场报错、拼出来的过滤器形状要对。</para>
/// </remarks>
public class PruneFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void 标签_空白一律视为不过滤(string? label)
    {
        PruneFilter.NormalizeLabel(label).Should().BeNull();
    }

    [Theory]
    [InlineData("key", "key")]
    [InlineData("key=value", "key=value")]
    [InlineData("  key=value  ", "key=value")]
    [InlineData("key=va lue", "key=va lue")]
    public void 标签_去掉首尾空白后原样透传(string label, string expected)
    {
        // 值里允许有空格（标签值本来就可以含空格），
        // 而 key / key=value 两种写法都直接交给守护进程解释——
        // 服务端重新解释就等于维护了第二套与 Docker 不一致的规则。
        PruneFilter.NormalizeLabel(label).Should().Be(expected);
    }

    [Theory]
    [InlineData("bad\nlabel")]
    [InlineData("bad\rlabel")]
    [InlineData("bad\tlabel")]
    [InlineData("bad\0label")]
    public void 标签_含控制字符_报错(string label)
    {
        var act = () => PruneFilter.NormalizeLabel(label);

        act.Should().Throw<BusinessException>().Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void 标签_超长_报错()
    {
        var label = new string('a', PruneFilter.MaxLabelLength + 1);

        var act = () => PruneFilter.NormalizeLabel(label);

        act.Should().Throw<BusinessException>().Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void 标签_恰好等于上限_接受()
    {
        var label = new string('a', PruneFilter.MaxLabelLength);

        PruneFilter.NormalizeLabel(label).Should().Be(label);
    }

    [Fact]
    public void 过滤器_每个取值的布尔值都是true()
    {
        var filters = PruneFilter.Build(("dangling", "true"), ("label", "k=v"));

        filters.Should().ContainKey("dangling");
        filters["dangling"]["true"].Should().BeTrue(
            "守护进程按「取值 → 布尔」解释，只有 true 的条目才算启用");
        filters["label"]["k=v"].Should().BeTrue();
    }

    [Fact]
    public void 过滤器_同名过滤合并到同一组()
    {
        var filters = PruneFilter.Build(("label", "a"), ("label", "b"));

        filters.Should().ContainKey("label");
        filters["label"].Should().HaveCount(2);
        filters["label"]["a"].Should().BeTrue();
        filters["label"]["b"].Should().BeTrue();
    }
}
