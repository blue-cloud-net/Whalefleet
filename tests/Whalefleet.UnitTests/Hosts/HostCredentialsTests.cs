using Whalefleet.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Hosts;

/// <summary>
/// <see cref="HostCredentials"/> 值对象的一致性校验测试。
/// </summary>
public class HostCredentialsTests
{
    private const string _tlsPayload = """{"caPath":"/certs/ca.pem","certPath":"/certs/cert.pem"}""";

    [Fact]
    public void None_无认证且无载荷()
    {
        var credentials = HostCredentials.None;

        credentials.Kind.Should().Be(DockerHostAuthKind.None);
        credentials.ConfigJson.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void FromJson_无认证且载荷为空_返回共享的None实例(string? payload)
    {
        var credentials = HostCredentials.FromJson(DockerHostAuthKind.None, payload);

        credentials.Should().BeSameAs(HostCredentials.None);
    }

    [Fact]
    public void FromJson_无认证却带载荷_抛出参数异常()
    {
        var act = () => HostCredentials.FromJson(DockerHostAuthKind.None, "{}");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromJson_带认证且载荷合法_保留原文()
    {
        var credentials = HostCredentials.FromJson(DockerHostAuthKind.Tls, _tlsPayload);

        credentials.Kind.Should().Be(DockerHostAuthKind.Tls);
        credentials.ConfigJson.Should().Be(_tlsPayload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json }")]
    public void FromJson_带认证但载荷非法_抛出参数异常(string? payload)
    {
        var act = () => HostCredentials.FromJson(DockerHostAuthKind.Tls, payload);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromJson_认证方式未知_抛出参数异常()
    {
        var act = () => HostCredentials.FromJson((DockerHostAuthKind)42, _tlsPayload);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 相等性_按认证方式与载荷判定()
    {
        var left = HostCredentials.FromJson(DockerHostAuthKind.Tls, _tlsPayload);
        var right = HostCredentials.FromJson(DockerHostAuthKind.Tls, _tlsPayload);
        var otherKind = HostCredentials.FromJson(DockerHostAuthKind.Basic, _tlsPayload);

        left.Should().Be(right);
        left.Should().NotBe(otherKind);
        left.Should().NotBe(HostCredentials.None);
    }

    [Fact]
    public void ToString_不泄漏载荷内容()
    {
        var credentials = HostCredentials.FromJson(DockerHostAuthKind.Basic, """{"user":"admin","password":"s3cret"}""");

        var text = credentials.ToString();

        text.Should().Contain(nameof(DockerHostAuthKind.Basic));
        text.Should().NotContain("s3cret");
        text.Should().NotContain("password");
    }
}
