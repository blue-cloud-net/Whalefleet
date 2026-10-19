using Whalefleet.Core.Registries;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Registries;

/// <summary>
/// <see cref="RegistryServerAddress"/> 的规范化测试。
/// </summary>
/// <remarks>
/// 这个值对象的存在意义是「同一个地址在库里、镜像引用里、Docker 认证配置里必须逐字一致」。
/// 因此测试重点不是「能不能解析」，而是<b>各种输入形态是否收敛到同一个值</b>——
/// 只要有一处漏了规范化，推镜像时就会拿 A 处的地址去请求 B 处的仓库，
/// 报出来的是权限错误而不是地址错误。
/// </remarks>
public class RegistryServerAddressTests
{
    [Theory]
    [InlineData("harbor.local", "harbor.local", null)]
    [InlineData("HARBOR.LOCAL", "harbor.local", null)]
    [InlineData("  harbor.local  ", "harbor.local", null)]
    public void Parse_纯主机名_去掉空白并转小写(string input, string expected, int? expectedPort)
    {
        var address = RegistryServerAddress.Parse(input);

        address.Value.Should().Be(expected);
        address.Host.Should().Be(expected);
        address.Port.Should().Be(expectedPort);
        address.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("harbor.local:5000", "harbor.local:5000", "harbor.local", 5000)]
    [InlineData("HARBOR.LOCAL:5000", "harbor.local:5000", "harbor.local", 5000)]
    [InlineData("localhost:5000", "localhost:5000", "localhost", 5000)]
    public void Parse_带端口_拆出主机与端口(string input, string expected, string host, int port)
    {
        var address = RegistryServerAddress.Parse(input);

        address.Value.Should().Be(expected);
        address.Host.Should().Be(host);
        address.Port.Should().Be(port);
    }

    /// <summary>
    /// 协议前缀与路径必须被剥掉：Docker 的 <c>AuthConfig.ServerAddress</c> 要的是裸地址。
    /// </summary>
    [Theory]
    [InlineData("https://harbor.local", "harbor.local")]
    [InlineData("http://harbor.local:5000", "harbor.local:5000")]
    [InlineData("https://harbor.local/v2/", "harbor.local")]
    [InlineData("https://harbor.local:5000/v2", "harbor.local:5000")]
    public void Parse_带协议与路径_剥到裸地址(string input, string expected)
    {
        RegistryServerAddress.Parse(input).Value.Should().Be(expected);
    }

    /// <summary>
    /// 同一地址的各种写法必须<b>收敛到同一个值</b>，否则相等性判定与唯一索引都会失准。
    /// </summary>
    [Fact]
    public void Parse_等价写法_收敛到同一值且相等()
    {
        var variants = new[]
        {
            "harbor.local:5000",
            "HARBOR.LOCAL:5000",
            "  https://harbor.local:5000/  ",
            "http://harbor.local:5000/v2/",
        };

        var parsed = variants.Select(RegistryServerAddress.Parse).ToArray();

        parsed.Should().OnlyContain(item => item.Value == "harbor.local:5000");
        parsed.Should().AllBeEquivalentTo(parsed[0]);
    }

    /// <summary>
    /// IPv6 必须加方括号——这是 Docker 自己的要求，不加会被当成「主机名里带冒号」。
    /// </summary>
    [Theory]
    [InlineData("[::1]", "::1", null)]
    [InlineData("[::1]:5000", "::1", 5000)]
    [InlineData("[fe80::1]:8443", "fe80::1", 8443)]
    public void Parse_IPv6_要求方括号并拆出主机(string input, string host, int? port)
    {
        var address = RegistryServerAddress.Parse(input);

        address.Host.Should().Be(host);
        address.Port.Should().Be(port);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1:5000")]
    public void Parse_未加括号的IPv6_报格式错(string input)
    {
        var act = () => RegistryServerAddress.Parse(input);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("harbor.local:")]
    [InlineData("harbor.local:0")]
    [InlineData("harbor.local:70000")]
    [InlineData("harbor.local:abc")]
    [InlineData("harbor..local")]
    [InlineData("-harbor.local")]
    [InlineData("harbor-.local")]
    [InlineData("harb or.local")]
    [InlineData("[::1")]
    public void Parse_非法地址_报格式错(string? input)
    {
        var act = () => RegistryServerAddress.Parse(input!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_超长地址_报格式错()
    {
        var tooLong = new string('a', RegistryServerAddress.MaxLength + 1);

        var act = () => RegistryServerAddress.Parse(tooLong);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("harbor.local:abc", false)]
    [InlineData("https://", false)]
    public void TryParse_非法输入_返回false且结果为null(string? input, bool expected)
    {
        RegistryServerAddress.TryParse(input, out var result).Should().Be(expected);
        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_合法输入_返回true且带出结果()
    {
        RegistryServerAddress.TryParse("https://Harbor.Local:5000/", out var result).Should().BeTrue();

        result.Should().NotBeNull();
        result!.Value.Should().Be("harbor.local:5000");
    }

    [Fact]
    public void 相等性_按规范化后的值判定()
    {
        var a = RegistryServerAddress.Parse("https://Harbor.Local:5000/");
        var b = RegistryServerAddress.Parse("harbor.local:5000");

        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
