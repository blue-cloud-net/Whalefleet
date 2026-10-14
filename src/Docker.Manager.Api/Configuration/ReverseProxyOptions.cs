namespace Docker.Manager.Api.Configuration;

/// <summary>
/// 反向代理配置（配置节 <c>ReverseProxy</c>）。
/// </summary>
/// <remarks>
/// <para><b>解决的问题</b>：应用部署在 HTTPS 反代之后时，
/// <c>Connection.RemoteIpAddress</c> 是<b>反代自己</b>的地址，
/// 于是所有用户的登录失败都会落进同一个限流桶——5 次失败后全体用户在 1 分钟内
/// 都无法登录。启用 <c>ForwardedHeaders</c> 中间件并声明可信代理后，
/// 客户端 IP 才能从 <c>X-Forwarded-For</c> 取回真实值。</para>
/// <para><b>为什么默认关闭</b>：<c>X-Forwarded-For</c> 可被客户端任意伪造。
/// 若在无白名单的情况下采信它，等于把限流键交给攻击者——
/// 每次请求换一个伪造 IP 就能无限重试。因此<b>不配置就不启用</b>，
/// 而不是「默认信任所有代理」。</para>
/// <para><b>两类白名单二选一</b>：<see cref="KnownProxies"/> 列举具体代理地址（精确）；
/// <see cref="KnownNetworks"/> 列举网段（适合 Kubernetes Pod 网段这类动态地址）。
/// 两者都为空时中间件不注册。</para>
/// </remarks>
public sealed class ReverseProxyOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// 可信代理的 IP 地址清单（与 <see cref="KnownNetworks"/> 二选一）。
    /// </summary>
    /// <remarks>
    /// 元素支持 <c>IPAddress</c> 可解析的全部写法（IPv4 / IPv6）。
    /// 无法解析的项在启动时即失败——静默忽略等于代理没被信任，
    /// 而症状是「限流对所有人一起生效」，排查成本极高。
    /// </remarks>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// 可信代理网段清单（CIDR 写法，如 <c>10.42.0.0/16</c>），适合地址动态的部署形态。
    /// </summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// 是否为响应写入 <c>X-Forwarded-Proto</c> / <c>X-Forwarded-For</c>。
    /// </summary>
    /// <remarks>
    /// 仅当应用<b>确实</b>位于反代之后才需要——直连场景下开启只是徒增开销与困惑。
    /// </remarks>
    public bool Enabled { get; set; }
}