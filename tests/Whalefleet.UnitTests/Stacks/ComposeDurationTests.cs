using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Stacks;

/// <summary>
/// <see cref="ComposeDuration"/> 的行为测试。
/// </summary>
/// <remarks>
/// 重点是<b>亚秒向上取整</b>（<c>500ms</c> → 1 秒而不是 0）——
/// 0 会被下游当成「没设置」，症状是健康检查按默认值跑，
/// 而用户明明写了 500ms。
/// </remarks>
public class ComposeDurationTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("1m", 60)]
    [InlineData("1h", 3600)]
    [InlineData("1m30s", 90)]
    [InlineData("1h30m", 5400)]
    [InlineData("2h45m30s", 9930)]
    [InlineData("500ms", 1)]
    [InlineData("1000ms", 1)]
    [InlineData("1500ms", 2)]
    [InlineData("30", 30)]
    public void 解析_合法时长_得到秒数(string text, int expected)
    {
        ComposeDuration.Parse(text, "path").Should().Be(expected);
    }

    [Fact]
    public void 解析_空值_返回null()
    {
        ComposeDuration.Parse(null, "path").Should().BeNull();
        ComposeDuration.Parse("   ", "path").Should().BeNull();
    }

    [Theory]
    [InlineData("30d")]
    [InlineData("1.5h")]
    [InlineData("-30s")]
    [InlineData("0s")]
    [InlineData("abc")]
    [InlineData("30x")]
    [InlineData("s30")]
    public void 解析_非法时长_报1904(string text)
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeDuration.Parse(text, "path"));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    [Fact]
    public void 解析_非法时长_消息里带字段路径()
    {
        var exception = Assert.Throws<BusinessException>(
            () => ComposeDuration.Parse("30d", "services.web.healthcheck.interval"));

        exception.Message.Should().Contain("services.web.healthcheck.interval");
    }
}
