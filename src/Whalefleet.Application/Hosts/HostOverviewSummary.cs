using Whalefleet.Application.HostTags;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机概览：前端「总览页 / 主机选择器 / 主机概览页」共用的**单一**视图。
/// </summary>
/// <param name="Id">主机对外标识，**等于主机名**（也是路径里的 <c>hostId</c>）。</param>
/// <param name="Name">主机名。</param>
/// <param name="Type">连接方式（<c>socket</c> / <c>tcp</c>）。</param>
/// <param name="Endpoint">端点原文。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Status">连通状态（<c>online</c> / <c>offline</c> / <c>unknown</c>）。</param>
/// <param name="Counts">资源计数；未取到（离线 / 列表接口）时为 <see langword="null"/>。</param>
/// <param name="Version">Docker 版本信息；未取到时为 <see langword="null"/>。</param>
/// <param name="Daemon">守护进程静态技术信息；未取到时为 <see langword="null"/>。</param>
/// <param name="Tags">挂在该主机上的标签；列表与概览都给（没有标签时为空数组）。</param>
/// <remarks>
/// <para><b>为什么 <see cref="Id"/> 是主机名而不是 <c>hosts.id</c>（Guid）</b>：这个结构喂的是前端，
/// 而前端的路由、切换逻辑、深链用的都是主机名。把 Guid 放进来会立刻多出一个「界面上的 id
/// 和 URL 里的 hostId 不是同一个东西」的坑。Guid 只用于权限外键与审计，不进对外契约。</para>
/// <para><b>为什么列表与概览共用一个形状</b>：前端把两者都装进同一个 <c>HostOverview</c> 类型——
/// 列表里缺的字段（<see cref="Counts"/> / <see cref="Version"/> / <see cref="Daemon"/>）
/// 本来就是「这趟没取」，用 <see langword="null"/> 表达恰好对得上界面上的「—」占位。
/// 若分成两个类型，前端每处都要写两次字段访问。</para>
/// <para><b>为什么标签在列表里也给</b>：标签是过滤条件，而过滤在服务端做；
/// 列表不带标签的话，界面就没法在行上显示「这台主机为什么被筛出来」。
/// 标签体积很小（两个短字符串），不像计数那样要额外调守护进程。</para>
/// <para><b>为什么没有「运行时长」</b>：Docker Engine API 不提供守护进程启动时刻，
/// 也不提供宿主 boot time（<c>/info</c> 的 <c>SystemTime</c> 是<b>当前时间</b>）。
/// 0.0.8–0.5.0 的 <c>startedAt</c> 因此恒为「已运行 0 秒」，0.6.0 直接删除而不换口径。</para>
/// <para><b>为什么离线不抛异常</b>：主机离线是**正常状态**（列表里就要显示它），
/// 不是故障。因此取不到数据时返回 <c>status=offline</c> + 空字段，而不是 502。</para>
/// </remarks>
public sealed record HostOverviewSummary(
    string Id,
    string Name,
    DockerHostType Type,
    string Endpoint,
    bool Enabled,
    HostConnectivityStatus Status,
    HostResourceCounts? Counts,
    HostVersionDetail? Version,
    HostDaemonDetail? Daemon,
    IReadOnlyList<HostTagRef> Tags,
    Guid? GroupId);

/// <summary>
/// 主机连通状态。
/// </summary>
/// <remarks>
/// <b>为什么需要 <see cref="Unknown"/></b>：探测失败的原因不止「连不上」——凭据过期、TLS 握手异常、
/// 守护进程返回了无法解释的错误，都可能发生。把这类情况一律报成 <c>offline</c> 会让人去查网络，
/// 而真正的病因在别处；<c>unknown</c> 至少诚实地说「没探明白」。
/// </remarks>
public enum HostConnectivityStatus
{
    /// <summary>尚未探测，或探测失败但原因不属于「连不上」。</summary>
    Unknown,

    /// <summary>可连通。</summary>
    Online,

    /// <summary>连不上（端点不可达，或主机已停用）。</summary>
    Offline,
}

/// <summary>
/// 主机上的资源计数。
/// </summary>
/// <param name="Containers">容器总数（含已停止）。</param>
/// <param name="Images">镜像总数。</param>
/// <param name="Volumes">数据卷总数。</param>
/// <param name="Networks">网络总数（含 Docker 自建的 bridge / host / none）。</param>
/// <param name="ContainersRunning">运行中的容器数（取自 <c>/info</c>）。</param>
/// <param name="ContainersStopped">已停止的容器数（取自 <c>/info</c>）。</param>
/// <param name="ContainersHealthy">健康检查通过的容器数。</param>
/// <param name="ContainersUnhealthy">健康检查失败的容器数。</param>
/// <param name="ImagesSizeBytes">
/// 镜像层实际占用的磁盘字节数；守护进程不支持 <c>/system/df?type=image</c> 或查询失败时为
/// <see langword="null"/>（「不知道」，与「0 字节」不同）。
/// </param>
/// <remarks>
/// <para>容器 / 镜像总数与运行 / 停止数取自 <c>/info</c>（一次调用就有）；卷与网络数必须各列一次
/// （Docker 的 <c>/info</c> 不提供这两个计数）。健康检查数只能从容器列表的 <c>Status</c>
/// 文本里读（<c>Up 2 hours (healthy)</c>），镜像磁盘占用只能从 <c>/system/df</c> 取——两者都
/// 是 <b>额外两次</b>调用。</para>
/// <para>这些调用只发生在「概览」这条路径上，列表接口不取：主机一多，列表若挨个拉这些计数
/// 就会明显变慢。前端的四类资源磁贴按需显示粗粒度数字，细分项（运行 / 健康 / 占用）只在
/// 主机概况页用到。</para>
/// <para><b><see cref="ContainersHealthy"/> 与 <see cref="ContainersUnhealthy"/> 之和通常小于
/// <see cref="Containers"/></b>：绝大多数容器没有配健康检查，「没配」既不是健康也不是不健康，
/// 不该被算进任何一边。</para>
/// </remarks>
public sealed record HostResourceCounts(
    long Containers,
    long Images,
    long Volumes,
    long Networks,
    long ContainersRunning,
    long ContainersStopped,
    long ContainersHealthy,
    long ContainersUnhealthy,
    long? ImagesSizeBytes);

/// <summary>
/// 主机上的 Docker 版本信息。
/// </summary>
/// <param name="Version">Docker 引擎版本（如 <c>27.3.1</c>）。</param>
/// <param name="ApiVersion">守护进程支持的 API 版本（如 <c>1.47</c>）。</param>
/// <param name="Os">宿主操作系统（<c>linux</c> / <c>windows</c>）。</param>
/// <param name="Arch">宿主架构（<c>x86_64</c> / <c>aarch64</c>）。</param>
/// <param name="GoVersion">构建守护进程所用的 Go 版本。</param>
/// <remarks>
/// 字段全部可空：守护进程理论上都会给，但「给不出来」用 <see langword="null"/> 比空串表达得更准——
/// 空串会显示成一个空白的版本号，而 <see langword="null"/> 能让界面走它的占位分支。
/// </remarks>
public sealed record HostVersionDetail(
    string? Version,
    string? ApiVersion,
    string? Os,
    string? Arch,
    string? GoVersion);
