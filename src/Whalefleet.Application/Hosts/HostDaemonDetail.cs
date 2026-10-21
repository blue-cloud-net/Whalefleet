namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机上 Docker 守护进程的静态技术信息（<c>/info</c> 的<b>精选</b>投影）。
/// </summary>
/// <param name="OperatingSystem">宿主操作系统发行版（如 <c>Ubuntu 24.04.5 LTS</c>）。</param>
/// <param name="OsVersion">系统版本号。</param>
/// <param name="OsType">系统类型（<c>linux</c> / <c>windows</c>）。</param>
/// <param name="Architecture">宿主架构。</param>
/// <param name="KernelVersion">内核版本。</param>
/// <param name="CpuCount">CPU 核数——<b>静态上限</b>，不是当前占用。</param>
/// <param name="MemoryBytes">物理内存总量——<b>静态上限</b>，不是当前占用。</param>
/// <param name="StorageDriver">存储驱动名（如 <c>overlayfs</c>）。</param>
/// <param name="StorageDriverStatus">存储驱动的状态明细（键值对数组）。</param>
/// <param name="LoggingDriver">默认日志驱动（如 <c>json-file</c>）。</param>
/// <param name="CgroupDriver">Cgroup 驱动（如 <c>systemd</c>）。</param>
/// <param name="CgroupVersion">Cgroup 版本（<c>1</c> / <c>2</c>）。</param>
/// <param name="DefaultRuntime">默认运行时（如 <c>runc</c>）。</param>
/// <param name="Runtimes">已注册的运行时名列表。</param>
/// <param name="InitBinary">init 二进制名（如 <c>docker-init</c>）。</param>
/// <param name="ContainerdVersion">containerd 版本（提交哈希）。</param>
/// <param name="RuncVersion">runc 版本。</param>
/// <param name="InitVersion">init 二进制版本（提交哈希）。</param>
/// <param name="Plugins">按类别分组的插件名列表。</param>
/// <param name="RegistryMirrors">镜像加速器地址。</param>
/// <param name="InsecureRegistries">被允许的不安全仓库 CIDR / 地址。</param>
/// <param name="IndexServerAddress">默认 registry 索引地址。</param>
/// <param name="DockerRootDir">Docker 数据根目录。</param>
/// <param name="SecurityOptions">启用的安全选项（apparmor / seccomp …）。</param>
/// <param name="LiveRestoreEnabled">是否启用 live-restore。</param>
/// <param name="FirewallBackend">防火墙后端（<c>iptables</c> / <c>nftables</c> …）。</param>
/// <param name="CdiSpecDirs">CDI spec 目录。</param>
/// <param name="HttpProxy">HTTP 代理（**已遮蔽 userinfo**）。</param>
/// <param name="HttpsProxy">HTTPS 代理（**已遮蔽 userinfo**）。</param>
/// <param name="NoProxy">不走代理的地址列表。</param>
/// <param name="Warnings">守护进程自报的告警。</param>
/// <remarks>
/// <para><b>为什么是精选投影而不是原样返回 <c>/info</c></b>：那个响应有上百个字段
/// （含 Swarm 状态、各驱动的内部计数），直接透传等于把「响应里有什么」交给 SDK 版本决定，
/// 前端也无法依赖任何字段。</para>
/// <para><b><see cref="CpuCount"/> / <see cref="MemoryBytes"/> 是静态上限</b>：
/// <c>/info</c> 只给 <c>NCPU</c> / <c>MemTotal</c>，Docker Engine API <b>没有</b>宿主机级
/// 实时指标端点。界面上不得把它们标成「当前 CPU / 内存」——那会让同一个页面出现两套口径。</para>
/// <para><b>不重复 <see cref="HostVersionDetail"/> 已有的字段</b>：<c>os</c> / <c>arch</c>
/// 已经由 <c>/version</c> 给出，这里不再重列，否则同一页两个位置可能显示不同的值。</para>
/// <para><b>不返回 Swarm 相关</b>：Swarm 属项目非目标。</para>
/// <para><b>代理 URL 必须遮蔽 userinfo</b>：<c>http://user:pass@host</c> 里的口令一旦进入
/// API 响应，就会随着浏览器缓存、日志采集器与截图一起扩散——与「密码永不进日志」同源。</para>
/// <para><b>不新增任何 Docker 调用</b>：全部字段都来自概览路径上已有的那次 <c>/info</c>，
/// 调用次数不变。</para>
/// </remarks>
public sealed record HostDaemonDetail(
    string? OperatingSystem,
    string? OsVersion,
    string? OsType,
    string? Architecture,
    string? KernelVersion,
    long CpuCount,
    long MemoryBytes,
    string? StorageDriver,
    IReadOnlyList<IReadOnlyList<string>> StorageDriverStatus,
    string? LoggingDriver,
    string? CgroupDriver,
    string? CgroupVersion,
    string? DefaultRuntime,
    IReadOnlyList<string> Runtimes,
    string? InitBinary,
    string? ContainerdVersion,
    string? RuncVersion,
    string? InitVersion,
    HostDaemonPlugins Plugins,
    IReadOnlyList<string> RegistryMirrors,
    IReadOnlyList<string> InsecureRegistries,
    string? IndexServerAddress,
    string? DockerRootDir,
    IReadOnlyList<string> SecurityOptions,
    bool LiveRestoreEnabled,
    string? FirewallBackend,
    IReadOnlyList<string> CdiSpecDirs,
    string? HttpProxy,
    string? HttpsProxy,
    string? NoProxy,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 守护进程已安装的插件（按类别分组）。
/// </summary>
/// <param name="Volume">卷插件名。</param>
/// <param name="Network">网络插件名。</param>
/// <param name="Log">日志驱动插件名。</param>
/// <param name="Authorization">授权插件名；未安装时为空列表。</param>
/// <remarks>
/// 空列表而不是 <see langword="null"/>：插件类别是固定四类，某一类没有插件是**确定的事实**，
/// 而 <see langword="null"/> 会被读成「没取到」。
/// </remarks>
public sealed record HostDaemonPlugins(
    IReadOnlyList<string> Volume,
    IReadOnlyList<string> Network,
    IReadOnlyList<string> Log,
    IReadOnlyList<string> Authorization);
