using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whalefleet.Api.Configuration;

/// <summary>
/// 按配置注册 <c>ForwardedHeaders</c> 中间件。
/// </summary>
/// <remarks>
/// <para><b>为什么默认不注册</b>：<c>X-Forwarded-For</c> 由客户端可控。
/// 没有可信代理白名单就采信它，等于让攻击者自己选择限流键——
/// 每次换一个伪造 IP 就能无限重试登录。白名单为空时不注册中间件，
/// <c>RemoteIpAddress</c> 保持为真实 TCP 对端。</para>
/// <para><b>为什么无法解析的地址要启动失败</b>：静默忽略一个写错的代理地址，
/// 症状是「反代后面所有人共用一个限流桶」，与「没配白名单」完全同形，
/// 排查时无从下手。因此配置错误必须响亮。</para>
/// </remarks>
internal static class ForwardedHeadersExtensions
{
    /// <summary>
    /// 读取 <c>ReverseProxy</c> 配置。
    /// </summary>
    /// <param name="configuration">配置根。</param>
    /// <returns>反代配置。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> 为 <see langword="null"/>。</exception>
    public static ReverseProxyOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ReverseProxyOptions();
        configuration.GetSection(ReverseProxyOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// 校验配置项可解析，并返回对应的地址与网段。
    /// </summary>
    /// <param name="options">反代配置。</param>
    /// <returns>可信代理地址与网段；未配置时均为空集合。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">某项无法解析为 IP 或 CIDR。</exception>
    /// <remarks>
    /// 网段用 <c>System.Net.IPNetwork</c>（.NET 10 随 BCL 提供）而非
    /// <c>Microsoft.AspNetCore.HttpOverrides.IPNetwork</c>——后者已标记过时（ASPDEPR005），
    /// 在本仓库「警告即错误」的约定下会直接挡住构建。
    /// </remarks>
    public static (List<IPAddress> Proxies, List<System.Net.IPNetwork> Networks) Resolve(
        ReverseProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var proxies = new List<IPAddress>(options.KnownProxies.Length);
        foreach (var raw in options.KnownProxies)
        {
            if (!IPAddress.TryParse(raw, out var address))
            {
                throw new InvalidOperationException(
                    $"ReverseProxy:KnownProxies 中的 '{raw}' 不是合法 IP 地址");
            }

            proxies.Add(address);
        }

        var networks = new List<System.Net.IPNetwork>(options.KnownNetworks.Length);
        foreach (var raw in options.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(raw, out var network))
            {
                throw new InvalidOperationException(
                    $"ReverseProxy:KnownNetworks 中的 '{raw}' 不是合法的 CIDR 写法（如 10.42.0.0/16）");
            }

            networks.Add(network);
        }

        return (proxies, networks);
    }

    /// <summary>
    /// 按配置注册 <c>UseForwardedHeaders</c>。
    /// </summary>
    /// <param name="app">应用管道构建器。</param>
    /// <param name="configuration">配置根。</param>
    /// <returns>同一 <paramref name="app"/> 实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 必须在管道<b>最前</b>调用：<c>RemoteIpAddress</c> 一旦被后续中间件读取并缓存，
    /// 修正就来不及了（限流计数与请求日志都依赖它）。
    /// </remarks>
    public static IApplicationBuilder UseConfiguredForwardedHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);
        if (!options.Enabled)
        {
            return app;
        }

        var (proxies, networks) = Resolve(options);
        if (proxies.Count == 0 && networks.Count == 0)
        {
            // 开了开关却没给白名单 = 采信任意来源的 X-Forwarded-For。
            // 这比不开还糟，因此直接拒绝启动。
            throw new InvalidOperationException(
                "ReverseProxy:Enabled 为 true，但 KnownProxies 与 KnownNetworks 均为空。"
                + "这会让任意客户端都能伪造 X-Forwarded-For，请至少配置一个可信代理。");
        }

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        // 不用 ASP.NET Core 的默认「信任一切」：清空后只保留下面显式加入的。
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();

        foreach (var proxy in proxies)
        {
            forwarded.KnownProxies.Add(proxy);
        }

        foreach (var network in networks)
        {
            forwarded.KnownIPNetworks.Add(network);
        }

        return app.UseForwardedHeaders(forwarded);
    }
}