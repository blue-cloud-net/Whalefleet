namespace Whalefleet.Application.Networks;

/// <summary>
/// 网络列表项。
/// </summary>
/// <param name="Id">网络 ID（<b>64 位十六进制，不带 <c>sha256:</c> 前缀</b>）。</param>
/// <param name="Name">网络名。</param>
/// <param name="Driver">网络驱动（<c>bridge</c> / <c>host</c> / <c>macvlan</c> / <c>overlay</c> 等）。</param>
/// <param name="Scope">作用域（<c>local</c> / <c>swarm</c>）。</param>
/// <param name="Attachable">是否可被 <c>docker run --network</c> 单独接入（Swarm 的覆盖网络默认为否）。</param>
/// <param name="Internal">是否为内部网络（不配外部路由，只有容器之间能互通）。</param>
/// <param name="Subnets">IPAM 子网（CIDR）。</param>
/// <param name="Gateways">各子网的网关地址。</param>
/// <param name="Labels">标签。</param>
/// <remarks>
/// <para><b>网络 ID 没有 <c>sha256:</c> 前缀</b>（镜像 ID 有）。
/// 这是 Docker 各资源 ID 格式不一致的一处：<c>/networks</c> 返回的是裸的 64 位十六进制。
/// 照搬镜像那套「凡是 ID 都先剥前缀」的解析逻辑会得到一个错的 ID，
/// 而错 ID 的表现是 404 ——极难联想到是前缀问题。</para>
/// <para><b>刻意<b>没有</b>「容器数」字段</b>：Docker 的 <c>GET /networks</c>（列表）
/// **不返回** <c>Containers</c>，而 <c>GET /networks/{id}</c>（详情）返回——
/// 这一点已实测确认。因此列表里的「容器数」只能恒为 0。
/// <br/>
/// 宁可不给，也不给一个永远错的：界面上「0 个容器」会被读成「这个网络没在用」，
/// 而它可能正承载着几十个容器。真正权威的接入情况请看
/// <see cref="NetworkDetail.Containers"/>（需打开详情）。</para>
/// <para><b>为什么同时给 <paramref name="Id"/> 与 <paramref name="Name"/></b>：
/// Docker 删除网络时两者都接受，但 <c>inspect</c> 与 <c>connect</c> 只认 ID。
/// 界面上用户看到并操作的是名字，而请求体里应该用稳定的 ID——
/// 名字允许改（虽然本项目暂未开放改名），ID 不会变。</para>
/// <para><b>子网与网关拆成两个列表而不是配对对象</b>：
/// <c>IPAM.Config</c> 在实践中通常只有一项（单池网络），
/// 而界面要的只是「子网是什么、网关是什么」两个可读字段。
/// 拆成两个列表后渲染代码不需要处理「IPAM 为空数组」的情况——
/// 那是自定义驱动（<c>macvlan</c>）的常态。</para>
/// </remarks>
public sealed record NetworkSummary(
    string Id,
    string Name,
    string Driver,
    string Scope,
    bool Attachable,
    bool Internal,
    IReadOnlyList<string> Subnets,
    IReadOnlyList<string> Gateways,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// 网络详情（inspect 的<b>精选</b>投影）。
/// </summary>
/// <param name="Id">网络 ID。</param>
/// <param name="Name">网络名。</param>
/// <param name="Driver">网络驱动。</param>
/// <param name="Scope">作用域。</param>
/// <param name="Attachable">是否可单独接入。</param>
/// <param name="Internal">是否为内部网络。</param>
/// <param name="EnableIPv4">是否启用 IPv4。</param>
/// <param name="EnableIPv6">是否启用 IPv6。</param>
/// <param name="Ipam">IPAM 配置（每个地址池一项）。</param>
/// <param name="Containers">已接入的容器端点。</param>
/// <param name="Options">驱动选项（如 <c>com.docker.network.bridge.name</c>）。</param>
/// <param name="Labels">标签。</param>
/// <remarks>
/// <para><b>端点带 IP 与 MAC 而不是一个光秃秃的容器 ID</b>：
/// 「这个网络里都有谁、各自拿到什么地址」是排查「容器互相访问不了」时最先要看的东西，
/// 只给 ID 等于还要用户自己去别处查。</para>
/// <para><b>刻意没有 <c>Peers</c> 与 <c>Services</c></b>：
/// 两者只在 Swarm 覆盖网络下有值，且本项目不提供 Swarm 管理（见
/// <c>docs/overview.md</c> 的非目标），照搬它们只会让响应体在非 Swarm 主机上多两个空数组。</para>
/// </remarks>
public sealed record NetworkDetail(
    string Id,
    string Name,
    string Driver,
    string Scope,
    bool Attachable,
    bool Internal,
    bool EnableIPv4,
    bool EnableIPv6,
    IReadOnlyList<NetworkIpam> Ipam,
    IReadOnlyList<NetworkContainerEndpoint> Containers,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// 网络的单个 IPAM 地址池。
/// </summary>
/// <param name="Subnet">子网（CIDR，如 <c>172.18.0.0/16</c>）。</param>
/// <param name="Gateway">该池的网关地址；守护进程未分配时为 <see langword="null"/>。</param>
/// <param name="IpRange">动态分配范围（<c>--ip-range</c>）；未指定时为 <see langword="null"/>。</param>
/// <remarks>
/// <b>三项都可为空</b>：自定义驱动（如 <c>macvlan</c>）可能完全不填 <c>Config</c>，
/// 而守护进程也会对某些池不回 <c>Gateway</c>。按可空写才不会在渲染时炸掉。
/// </remarks>
public sealed record NetworkIpam(string? Subnet, string? Gateway, string? IpRange);

/// <summary>
/// 已接入某网络的容器端点。
/// </summary>
/// <param name="Id">容器 ID。</param>
/// <param name="Name">容器名。</param>
/// <param name="MacAddress">MAC 地址；未分配时为 <see langword="null"/>。</param>
/// <param name="Ipv4Address">IPv4 地址（形如 <c>172.18.0.2/16</c>，含前缀长度）。</param>
/// <param name="Ipv6Address">IPv6 地址（形如 <c>2001:db8::2/64</c>）。</param>
public sealed record NetworkContainerEndpoint(
    string Id,
    string Name,
    string? MacAddress,
    string? Ipv4Address,
    string? Ipv6Address);

/// <summary>
/// 网络列表结果。
/// </summary>
/// <param name="Items">全部网络（<b>不分页</b>）。</param>
/// <remarks>
/// 与 <c>VolumeListResult</c> 同一口径：单台主机上的网络是个位数到几十条，
/// 条目又只有几十字节，不值得切分；分页与筛选统一归 <c>0.16.0</c>。
/// 与数据卷不同的是<b>这里没有 <c>Warnings</c></b>——
/// Docker 的 <c>GET /networks</c> 响应里没有 <c>warnings</c> 字段，硬加一个只能是永远为空。
/// </remarks>
public sealed record NetworkListResult(IReadOnlyList<NetworkSummary> Items);
