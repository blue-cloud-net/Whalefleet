namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 容器资源采样的共享采样器（0.6.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：<c>0.5.0</c> 的实现是「每个客户端订阅 = 一条到守护进程的长连接」。
/// 列表页打开指标列后一次要订阅 20 个容器，再打开详情页就是 21 条；同一个人刷新页面、
/// 另一个人同时在看同一台机器时，连接数会继续翻倍。采样器把「一条容器一条流」做成
/// <b>进程内共享</b>：无论多少订阅者，一台容器只占一条到 Docker 的流。</para>
/// <para><b>为什么带环形缓冲</b>：新订阅者如果从零开始，sparkline 会先空上一分钟。
/// 保留最近 60 点（约两分钟）并在订阅时<b>先重放</b>，图立刻就成型——
/// 这也是 roadmap 里「采样缓存」那条要求。</para>
/// <para><b>不落库</b>：历史指标持久化是明确非目标（那需要时序存储）。</para>
/// </remarks>
public interface IContainerStatsSampler
{
    /// <summary>
    /// 订阅某个容器的采样（先历史、后实时）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识。</param>
    /// <returns>订阅句柄；<b>必须</b> <see cref="IAsyncDisposable.DisposeAsync"/> 释放。</returns>
    /// <remarks>
    /// 同步返回：这里没有任何 IO——起泵是后台任务，历史缓冲是内存快照。
    /// 订阅与快照在同一个锁内完成，因此历史与实时流<b>既不重叠也不留缝</b>。
    /// </remarks>
    ContainerStatsSubscription Subscribe(string hostId, string containerId);
}
