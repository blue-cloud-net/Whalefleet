using FluentAssertions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerFactAttribute"/> 的跳过契约。
/// </summary>
/// <remarks>
/// 这套测试基础设施唯一的职责是「守护进程不可达时跳过、可达时不跳过」。
/// 它错了的后果很有欺骗性：要么该跳的用例失败（把人逼到开始无视红灯），
/// 要么该跑的用例被静默跳过（覆盖率为零却显示全绿）。因此这里明确断言两个方向。
/// </remarks>
public class DockerFactAttributeTests
{
    [Fact]
    public void 可用性探测可重复调用且不抛异常()
    {
        // 探测发生在特性构造期：一旦抛异常，整个测试会话都跑不起来。
        // 结果按 Lazy 缓存，因此连续调用必须一致（也说明没有被重复探测拖慢）。
        var first = DockerAvailability.IsAvailable;
        var second = DockerAvailability.IsAvailable;

        second.Should().Be(first);
    }

    [Fact]
    public void 跳过与否必须与探测结果一致()
    {
        var attribute = new DockerFactAttribute();

        if (DockerAvailability.IsAvailable)
        {
            attribute.Skip.Should().BeNull("守护进程可用时不应跳过");
        }
        else
        {
            attribute.Skip.Should().NotBeNullOrWhiteSpace("不可用时必须给出跳过原因，否则报告里只看到一个空白的跳过");
        }
    }

    [Fact]
    public void 理论版与事实版行为一致()
    {
        var theory = new DockerTheoryAttribute();

        (theory.Skip is null).Should().Be(DockerAvailability.IsAvailable);
    }

    [Fact]
    public void 跳过原因要指明是哪个socket()
    {
        // CI 日志里只有这一行能说明为什么跳过，因此必须自解释。
        DockerAvailability.SkipReason.Should().Contain(DockerAvailability.DefaultSocketPath);
        DockerAvailability.SkipReason.Should().Contain("跳过");
    }
}
