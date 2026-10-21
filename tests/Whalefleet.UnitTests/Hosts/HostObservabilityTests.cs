using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.HostTags;
using Whalefleet.Application.Hosts;
using Whalefleet.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Hosts;

/// <summary>
/// 0.6.0 主机域的三块纯逻辑：标签不变量、代理凭据遮蔽、健康状态解析。
/// </summary>
/// <remarks>
/// 三者都是「错了也不会报错、只会静默显示错东西」的那类代码，因此逐条钉住：
/// 标签名/颜色的边界、遮蔽后不带 userinfo、<c>(unhealthy)</c> 不被 <c>(healthy)</c> 抢先匹配。
/// </remarks>
public class HostObservabilityTests
{
    [Theory]
    [InlineData("prod", "prod")]
    [InlineData("  prod  ", "prod")]
    public void 标签名去首尾空白(string input, string expected)
    {
        var tag = HostTag.Create(Guid.NewGuid(), input, null, null);

        tag.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\tb")]
    public void 非法标签名被拒绝(string name)
    {
        var act = () => HostTag.Create(Guid.NewGuid(), name, null, null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 标签名超过上限被拒绝()
    {
        var name = new string('x', HostTag.MaxNameLength + 1);

        var act = () => HostTag.Create(Guid.NewGuid(), name, null, null);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("#1a2b3c", "#1A2B3C")]
    [InlineData("  #ABCDEF  ", "#ABCDEF")]
    public void 颜色规范化为大写六位十六进制(string input, string expected)
    {
        var tag = HostTag.Create(Guid.NewGuid(), "color", null, input);

        tag.Color.Should().Be(expected);
    }

    [Theory]
    [InlineData("#abc")]
    [InlineData("red")]
    [InlineData("1a2b3c")]
    [InlineData("#GGGGGG")]
    public void 非法颜色被拒绝(string color)
    {
        var act = () => HostTag.Create(Guid.NewGuid(), "color", null, color);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 颜色可以清空()
    {
        var tag = HostTag.Create(Guid.NewGuid(), "color", null, "#112233");

        tag.SetColor("  ");

        tag.Color.Should().BeNull();
    }

    [Theory]
    [InlineData("http://user:pass@proxy:3128", "http://proxy:3128")]
    [InlineData("http://user@proxy:3128", "http://proxy:3128")]
    [InlineData("http://proxy:3128", "http://proxy:3128")]
    [InlineData("proxy.internal:3128", "proxy.internal:3128")]
    public void 代理地址遮蔽userinfo(string input, string expected)
    {
        HostProxyMask.Mask(input).Should().Be(expected);
    }

    [Fact]
    public void 未配置代理返回null()
    {
        HostProxyMask.Mask(null).Should().BeNull();
        HostProxyMask.Mask("   ").Should().BeNull();
    }

    [Theory]
    [InlineData("Up 2 hours (healthy)", ContainerHealthStatus.Healthy)]
    [InlineData("Up 10 seconds (health: starting)", ContainerHealthStatus.Starting)]
    [InlineData("Up 1 minute (unhealthy)", ContainerHealthStatus.Unhealthy)]
    [InlineData("Up 3 hours", ContainerHealthStatus.None)]
    [InlineData("Exited (0) 2 minutes ago", ContainerHealthStatus.None)]
    [InlineData(null, ContainerHealthStatus.None)]
    public void 列表状态文本解析健康状态(string? status, ContainerHealthStatus expected)
    {
        ContainerHealth.FromStatusText(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("healthy", ContainerHealthStatus.Healthy)]
    [InlineData("unhealthy", ContainerHealthStatus.Unhealthy)]
    [InlineData("starting", ContainerHealthStatus.Starting)]
    [InlineData("none", ContainerHealthStatus.None)]
    [InlineData(null, ContainerHealthStatus.None)]
    public void inspect状态解析健康状态(string? status, ContainerHealthStatus expected)
    {
        ContainerHealth.FromInspect(status).Should().Be(expected);
    }

    [Fact]
    public void 标签引用投影只保留展示需要的字段()
    {
        var tag = HostTag.Create(Guid.NewGuid(), "gpu", "描述不应出现在主机列表上", "#22C55E");

        var reference = HostTagService.ToRef(tag);

        reference.Id.Should().Be(tag.Id);
        reference.Name.Should().Be("gpu");
        reference.Color.Should().Be("#22C55E");
    }

    [Fact]
    public void 业务错误码与标签有关的两项已登记()
    {
        // 未登记的错误码会被兜底成 Internal / 500，前端就再也分不清「标签不存在」与「服务器炸了」
        ErrorCodeCatalog.Classify(ErrorCodes.HostTagNotFound).Should().Be(ErrorCategory.NotFound);
        ErrorCodeCatalog.Classify(ErrorCodes.HostTagNameAlreadyExists).Should().Be(ErrorCategory.Validation);
    }
}
