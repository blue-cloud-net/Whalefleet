namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 容器资源采样流。
/// </summary>
/// <remarks>
/// <para><b>与日志流分开</b>：两者的频率、生命周期与背压完全不同——
/// 日志是「有就推、可能很密」，采样是「固定节奏、永远不空」。
/// 合成一个流会让客户端必须同时处理「可能十秒没有一帧」与「一秒十帧」两种节奏。</para>
/// <para><b>为什么换算在服务端做</b>：见 <see cref="ContainerStatsSample"/> 的说明——
/// CPU 百分比必须由相邻两帧相减得到，公式放在服务端才能保证所有客户端口径一致。</para>
/// </remarks>
public interface IContainerStatsProvider
{
    /// <summary>
    /// 跟随容器资源采样，逐次产出。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识（完整 ID / 短 ID / 名称）。</param>
    /// <param name="cancellationToken">取消令牌；触发后枚举结束并关闭 Docker 侧的流。</param>
    /// <returns>逐次产出的采样结果。</returns>
    /// <exception cref="Common.BusinessException">
    /// 容器不存在（<c>1401</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    IAsyncEnumerable<ContainerStatsSample> StreamStatsAsync(
        string hostId,
        string containerId,
        CancellationToken cancellationToken = default);
}
