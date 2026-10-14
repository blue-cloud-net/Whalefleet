using Whalefleet.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Hosts;

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

        // Value 是要落库 / 发请求的原文，必须原样保留
        endpoint.Value.Should().Be(value);

        // ToString() 是脱敏形态：下面四个都不含 userinfo，因此与原文一致。
        // 含 userinfo 的行为见 脱敏_* 用例——那是刻意不同的。
        endpoint.ToString().Should().Be(HostEndpoint.Redact(value));
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

    /// <summary>
    /// 端点脱敏（0.0.14 审计）。
    /// </summary>
    /// <remarks>
    /// 端点是凭据载体：<c>tcp://admin:s3cr3t@host</c> 这种写法在
    /// <c>DockerEndpointNormalizer</c> 里会被明确拒绝（其错误消息就写着
    /// 「端点会进日志」），但那层在 Infrastructure 且只在**连接时**才跑得到——
    /// 而 <c>HostService.CreateAsync</c> 在那之前就已经把它写进日志与数据库了。
    ///
    /// 因此这里给第二道防线：<c>ToString()</c> 永远不给 userinfo，
    /// 让「不小心把对象塞进结构化日志」不再等价于凭据泄漏。
    /// </remarks>
    public class 脱敏
    {
        [Fact]
        public void 含账号密码的端点_ToString_不泄露密码()
        {
            var endpoint = new HostEndpoint("tcp://admin:s3cr3t@10.0.0.5:2376");

            endpoint.ToString().Should().Be("tcp://***@10.0.0.5:2376");
            endpoint.ToString().Should().NotContain("s3cr3t");
            endpoint.ToString().Should().NotContain("admin");
        }

        [Fact]
        public void 含账号密码的端点_Value_仍是原文()
        {
            // 脱敏只影响 ToString：要真正发请求 / 落库的地方读的是 Value
            var endpoint = new HostEndpoint("tcp://admin:s3cr3t@10.0.0.5:2376");

            endpoint.Value.Should().Be("tcp://admin:s3cr3t@10.0.0.5:2376");
        }

        [Theory]
        [InlineData("tcp://admin:s3cr3t@10.0.0.5:2376", "tcp://***@10.0.0.5:2376")]
        [InlineData("https://admin@10.0.0.5:2376", "https://***@10.0.0.5:2376")]
        [InlineData("tcp://10.0.0.5:2375", "tcp://10.0.0.5:2375")]
        [InlineData("/var/run/docker.sock", "/var/run/docker.sock")]
        [InlineData("unix:///var/run/docker.sock", "unix:///var/run/docker.sock")]
        public void Redact_按形态处理(string input, string expected)
        {
            HostEndpoint.Redact(input).Should().Be(expected);
        }

        [Fact]
        public void Redact_路径里的at不被当成userinfo()
        {
            // 卷名 data@home 与 /data@home 这类形态是合法的，
            // 一刀切删掉整段会把端点改得无法使用
            HostEndpoint.Redact("tcp://host:2375/data@home")
                .Should().Be("tcp://host:2375/data@home");
        }

        [Fact]
        public void Redact_查询串里的at不被当成userinfo()
        {
            HostEndpoint.Redact("tcp://host:2375/?scope=a@b")
                .Should().Be("tcp://host:2375/?scope=a@b");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Redact_空值不抛异常(string? input)
        {
            var act = () => HostEndpoint.Redact(input);

            act.Should().NotThrow();
        }
    }
}
