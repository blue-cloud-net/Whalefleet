namespace Docker.Manager.Application.Hosts;

/// <summary>
/// 主机概览（Docker 版本 + 运行摘要）。
/// </summary>
/// <param name="Version">守护进程版本信息（来自 <c>/version</c>）。</param>
/// <param name="Runtime">运行摘要（来自 <c>/info</c>）。</param>
/// <remarks>
/// <b>为什么拆成两块而不是揉成一个平铺对象</b>：它们来自守护进程的两个不同接口，
/// 字段重名也不少（<c>Version</c> / <c>ServerVersion</c>、<c>Os</c> / <c>OperatingSystem</c>）。
/// 分组之后调用方一眼能看出「这个字段来自哪个接口」，0.3.0 若给其中一块加缓存也不必动契约。
/// </remarks>
public sealed record HostOverviewSummary(HostVersionDetail Version, HostRuntimeDetail Runtime);

/// <summary>
/// 守护进程版本信息。
/// </summary>
/// <param name="Version">Docker 引擎版本（如 <c>27.3.1</c>）。</param>
/// <param name="ApiVersion">守护进程支持的 API 版本（如 <c>1.47</c>）。</param>
/// <param name="MinApiVersion">仍兼容的最低 API 版本。</param>
/// <param name="Os">宿主操作系统（<c>linux</c> / <c>windows</c>）。</param>
/// <param name="Arch">宿主架构（<c>x86_64</c> / <c>aarch64</c>）。</param>
/// <param name="GoVersion">构建守护进程所用的 Go 版本。</param>
/// <param name="KernelVersion">宿主内核版本。</param>
/// <param name="Experimental">守护进程是否以实验特性模式运行。</param>
/// <remarks>
/// <para>缺失字段一律回退成空串而不是 <see langword="null"/>：这些都是「展示用」的文本，
/// 前端少写一层判空就让页面少一处「undefined」。</para>
/// </remarks>
public sealed record HostVersionDetail(
    string Version,
    string ApiVersion,
    string MinApiVersion,
    string Os,
    string Arch,
    string GoVersion,
    string KernelVersion,
    bool Experimental);

/// <summary>
/// 主机运行摘要。
/// </summary>
/// <param name="Name">主机名（守护进程自报的 hostname）。</param>
/// <param name="ServerVersion">引擎版本（与 <see cref="HostVersionDetail.Version"/> 同源，便于单独使用本块）。</param>
/// <param name="StartedAt">守护进程启动时刻；拿不到时为 <see langword="null"/>。</param>
/// <param name="UptimeSeconds">已运行秒数；<see cref="StartedAt"/> 为空时为 <see langword="null"/>。</param>
/// <param name="Containers">容器总数。</param>
/// <param name="ContainersRunning">运行中容器数。</param>
/// <param name="ContainersPaused">已暂停容器数。</param>
/// <param name="ContainersStopped">已停止容器数。</param>
/// <param name="Images">镜像总数。</param>
/// <param name="Cpus">CPU 核数。</param>
/// <param name="MemoryTotalBytes">物理内存总量（字节）。</param>
/// <param name="OperatingSystem">操作系统名称（如 <c>Ubuntu 24.04.1 LTS</c>）。</param>
/// <remarks>
/// <b>为什么由服务端算 <see cref="UptimeSeconds"/></b>：让每个客户端各算一次会把「前端时钟不准」
/// 变成「主机运行时长是负的」，而这是最容易让人怀疑后端数据有问题的假象。
/// </remarks>
public sealed record HostRuntimeDetail(
    string Name,
    string ServerVersion,
    DateTimeOffset? StartedAt,
    long? UptimeSeconds,
    long Containers,
    long ContainersRunning,
    long ContainersPaused,
    long ContainersStopped,
    long Images,
    long Cpus,
    long MemoryTotalBytes,
    string OperatingSystem);
