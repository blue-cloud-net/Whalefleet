namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 一次容器资源采样。
/// </summary>
/// <remarks>
/// <para><b>为什么是「一次快照」而不是累计值</b>：Docker 的 stats 接口给出的是
/// <c>cpu_stats</c> / <c>precpu_stats</c> 这类<b>累计计数</b>，CPU 百分比必须由相邻两帧相减得到。
/// 把换算放在服务端做，客户端拿到的就是可直接显示的百分比——
/// 否则每家的换算公式稍有不同，同一台机器会显示出不同的 CPU 占用。</para>
/// <para><b>为什么网络是收发两个方向</b>：容器的「网络 I/O」在运维里通常问的是两个方向
/// （下载了多少、传了多少），合成一个数字会丢掉「只出不进」这类排查线索。</para>
/// </remarks>
/// <param name="ContainerId">容器标识。</param>
/// <param name="CpuPercent">CPU 占用百分比（0–100，可超过 100 表示多核并行）。</param>
/// <param name="MemoryUsage">内存占用字节数。</param>
/// <param name="MemoryLimit">内存上限字节数；守护进程未设限时为 0。</param>
/// <param name="NetworkRxBytes">累计接收字节数。</param>
/// <param name="NetworkTxBytes">累计发送字节数。</param>
/// <param name="Timestamp">采样时刻（UTC）。</param>
public sealed record ContainerStatsSample(
    string ContainerId,
    double CpuPercent,
    long MemoryUsage,
    long MemoryLimit,
    long NetworkRxBytes,
    long NetworkTxBytes,
    DateTimeOffset Timestamp);
