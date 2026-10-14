using System.Net;
using System.Net.Sockets;
using Whalefleet.Api.Configuration;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Configuration;

/// <summary>
/// <c>ReverseProxy</c> 配置解析的测试（0.0.15）。
/// </summary>
/// <remarks>
/// <para><b>为什么放在集成测试项目</b>：本类是 <c>internal</c> 且位于 Api 程序集，
/// 而仓库约定「UnitTests 不得引用 Api / Infrastructure」。纯逻辑测试也不例外——
/// 分层铁律优先于「这看起来只是解析字符串」。</para>
/// <para>这段逻辑的错误代价特别高，且症状具有欺骗性：代理地址写错、被静默忽略之后，
/// 表现是「反代后面所有人的登录失败共用一个限流桶」——
/// 与「压根没配白名单」完全同形，只能靠启动失败来区分。
/// 因此这里逐条钉住「该报错的必须报错」。</para>
/// </remarks>
public sealed class ReverseProxyOptionsTests
{
    /// <summary>
    /// 未配置任何代理时解析出空集合，而不是抛异常。
    /// </summary>
    [Fact]
    public void Resolve_WithEmptyConfiguration_ShouldReturnEmptyCollections()
    {
        var options = new ReverseProxyOptions();

        var (proxies, networks) = ForwardedHeadersExtensions.Resolve(options);

        proxies.Should().BeEmpty();
        networks.Should().BeEmpty();
    }

    /// <summary>
    /// 合法的 IPv4 / IPv6 代理地址应被正确解析。
    /// </summary>
    [Fact]
    public void Resolve_WithValidProxies_ShouldParseBothAddressFamilies()
    {
        var options = new ReverseProxyOptions
        {
            KnownProxies = ["10.0.0.5", "::1", "fd00::2"],
        };

        var (proxies, _) = ForwardedHeadersExtensions.Resolve(options);

        proxies.Should().HaveCount(3);

        // 注意：连续两次 proxies.Should().X 是对**整个集合**断言，
        // 要按条件筛选必须先 Where——写成 ContainSingle / HaveCount(2)
        // 会去数全部 3 个，从而得到一个看似合理实则错误的断言。
        proxies.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Should().ContainSingle();
        proxies.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
            .Should().HaveCount(2);
    }

    /// <summary>
    /// CIDR 网段应被解析，且保留前缀长度。
    /// </summary>
    [Fact]
    public void Resolve_WithValidNetworks_ShouldPreservePrefixLength()
    {
        var options = new ReverseProxyOptions
        {
            KnownNetworks = ["10.42.0.0/16", "172.16.0.0/12"],
        };

        var (_, networks) = ForwardedHeadersExtensions.Resolve(options);

        networks.Should().HaveCount(2);
        networks[0].PrefixLength.Should().Be(16);
        networks[1].PrefixLength.Should().Be(12);
    }

    /// <summary>
    /// 代理地址不是合法 IP 时必须抛异常——静默忽略会让部署者无法察觉配错了。
    /// </summary>
    /// <param name="raw">待测的地址文本。</param>
    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.256")]
    [InlineData("")]
    public void Resolve_WithInvalidProxy_ShouldThrow(string raw)
    {
        var options = new ReverseProxyOptions { KnownProxies = [raw] };

        var act = () => ForwardedHeadersExtensions.Resolve(options);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{raw}*");
    }

    /// <summary>
    /// 网段缺前缀长度、前缀非法或超出地址族上限时都必须抛异常。
    /// </summary>
    /// <param name="raw">待测的 CIDR 文本。</param>
    [Theory]
    [InlineData("10.42.0.0")]      // 缺 /prefix
    [InlineData("10.42.0.0/")]     // 前缀为空
    [InlineData("10.42.0.0/abc")]  // 前缀不是数字
    [InlineData("10.42.0.0/33")]   // IPv4 前缀上限是 32
    [InlineData("fd00::/129")]     // IPv6 前缀上限是 128
    [InlineData("garbage/24")]
    public void Resolve_WithInvalidNetwork_ShouldThrow(string raw)
    {
        var options = new ReverseProxyOptions { KnownNetworks = [raw] };

        var act = () => ForwardedHeadersExtensions.Resolve(options);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{raw}*");
    }

    /// <summary>
    /// 「/32」这类边界值是合法的，不能被上限检查误伤。
    /// </summary>
    [Fact]
    public void Resolve_WithBoundaryPrefixLengths_ShouldAccept()
    {
        var options = new ReverseProxyOptions
        {
            KnownNetworks = ["10.42.0.5/32", "fd00::1/128"],
        };

        var act = () => ForwardedHeadersExtensions.Resolve(options);

        act.Should().NotThrow();
    }

    /// <summary>
    /// 构造配置默认值应处于「关闭且无白名单」的安全默认。
    /// </summary>
    /// <remarks>
    /// 这条是防止有人「顺手把默认值改成 true」——
    /// 那样任何未显式配置的部署都会开始采信任意来源的 <c>X-Forwarded-For</c>，
    /// 等于把登录限流的键交给攻击者。
    /// </remarks>
    [Fact]
    public void Defaults_ShouldBeDisabledWithNoTrustedProxies()
    {
        var options = new ReverseProxyOptions();

        options.Enabled.Should().BeFalse();
        options.KnownProxies.Should().BeEmpty();
        options.KnownNetworks.Should().BeEmpty();
    }

    /// <summary>
    /// <c>Resolve</c> 对 null 立即失败，而不是抛 NullReferenceException。
    /// </summary>
    [Fact]
    public void Resolve_WithNull_ShouldThrowArgumentNullException()
    {
        var act = () => ForwardedHeadersExtensions.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// 开启开关却没有白名单时必须启动失败。
    /// </summary>
    /// <remarks>
    /// 「开了开关 = 采信所有来源的 X-Forwarded-For」比「不开」更糟：
    /// 不开只是反代后面的限流不准，开了则任何直连客户端都能自己挑限流键。
    /// </remarks>
    [Fact]
    public void EnabledWithoutWhitelist_ShouldFailToStart()
    {
        using var factory = new ApiFactory()
            .WithSetting("ReverseProxy:Enabled", "true");

        // CreateClient() 会同步构建宿主，因此中间件的注册期异常在这里直接抛出。
        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*KnownProxies*");
    }
}