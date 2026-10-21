namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 容器日志流（实时跟随）。
/// </summary>
/// <remarks>
/// <para><b>为什么是接口而不是具体类</b>：读日志要访问 Docker 守护进程，
/// 而「守护进程」是外部资源——按本项目的分层判据，它的抽象属于应用层。</para>
/// <para><b>为什么返回 <see cref="IAsyncEnumerable{T}"/></b>：日志是<b>持续产生</b>的，
/// 返回一个「等它读完」的 <c>Task</c> 表达不了「读到哪儿算哪儿」。
/// 异步枚举让调用方（Hub 的推送泵）可以逐行消费、随时取消。</para>
/// <para><b>取消即关闭</b>：调用方取消令牌被触发时，枚举必须结束，
/// 同时关掉 Docker 侧的那条流——否则会留下一个仍在读日志的后台任务。</para>
/// </remarks>
public interface IContainerLogProvider
{
    /// <summary>
    /// 跟随容器日志，逐行产出。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识（完整 ID / 短 ID / 名称）。</param>
    /// <param name="options">流选项（起始序号、起始时间、是否含 stderr）。</param>
    /// <param name="cancellationToken">取消令牌；触发后枚举结束并关闭 Docker 侧的流。</param>
    /// <returns>逐行产出的日志行。</returns>
    /// <exception cref="Common.BusinessException">
    /// 容器不存在（<c>1401</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    IAsyncEnumerable<ContainerLogLine> StreamLogsAsync(
        string hostId,
        string containerId,
        ContainerLogStreamOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 导出容器日志（一次性读完，逐行产出）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识（完整 ID / 短 ID / 名称）。</param>
    /// <param name="since">起始时刻（Unix 秒）；只导出该时刻之后的日志。</param>
    /// <param name="includeStderr">是否包含 stderr。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐行产出的日志行（<b>不含</b>时间戳）。</returns>
    /// <remarks>
    /// <para><b>与 <see cref="StreamLogsAsync"/> 的区别</b>：这里<b>不跟随</b>（读完即止）、
    /// <b>不编行号</b>、<b>不取时间戳</b>——它要的是「能存成文件的那份日志」，
    /// 与界面上看到的形态无关。</para>
    /// <para><b>为什么不受 tail 上限约束</b>：下载是「把完整日志拿走」，
    /// 而 <c>tail</c> 上限是为了约束<b>界面首屏</b>的响应体大小。
    /// 用一个上限卡住下载，用户会拿到一份被静默截断的文件——那比不给下载更糟。</para>
    /// </remarks>
    IAsyncEnumerable<string> ExportLogsAsync(
        string hostId,
        string containerId,
        long? since = null,
        bool includeStderr = true,
        CancellationToken cancellationToken = default);
}
