using Docker.Manager.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Hosts;

/// <summary>
/// <see cref="HostEndpoint"/> 值对象的校验与相等性测试。
/// </summary>
/// <remarks>
/// 端点与连接方式是否匹配不在此处判断（那是 <c>DockerHost</c> 与
/// <c>DockerEndpointNormalizer</c> 的职责），这里只覆盖值对象自身的约束。
/// </remarks>
public class HostEndpointTests
{
    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("tcp://host:2375")]
    [InlineData("npipe://./pipe/docker_engine")]
    public void 构造_合法端点_保留原文(string value)
    {
        var endpoint = new HostEndpoint(value);

        endpoint.Value.Should().Be(value);
        endpoint.ToString().Should().Be(value);
    }

    [Fact]
    public void 构造_去掉首尾空白()
    {
        var endpoint = new HostEndpoint("  /var/run/docker.sock  ");

        endpoint.Value.Should().Be("/var/run/docker.sock");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tcp://host:2375 with space")]
    [InlineData("/var/run/docker\t.sock")]
    public void 构造_非法端点_抛出参数异常(string value)
    {
        var act = () => new HostEndpoint(value);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 构造_超长端点_抛出参数异常()
    {
        var value = "/" + new string('a', HostEndpoint.MaxLength);

        var act = () => new HostEndpoint(value);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 相等性_按端点原文判定()
    {
        var left = new HostEndpoint("/var/run/docker.sock");
        var right = new HostEndpoint("/var/run/docker.sock");
        var other = new HostEndpoint("unix:///var/run/docker.sock");

        left.Should().Be(right);
        (left == right).Should().BeTrue();
        left.GetHashCode().Should().Be(right.GetHashCode());

        left.Should().NotBe(other);
        (left != other).Should().BeTrue();
    }
}
