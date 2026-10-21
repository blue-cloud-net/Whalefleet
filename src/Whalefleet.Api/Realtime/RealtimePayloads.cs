using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Realtime;

/// <summary>
/// Hub 动作（订阅 / 退订）的结果。
/// </summary>
/// <remarks>
/// <para><b>为什么不抛 <c>HubException</c></b>：SignalR 的 <c>HubException</c> 只能带一个字符串，
/// 客户端拿不到错误码。而本项目的契约是「错误码原样回带」——
/// 前端要靠 <c>1502</c>（缺权限）与 <c>1401</c>（容器不存在）分别给出不同提示，
/// 靠解析字符串是做不到的。</para>
/// <para><b>为什么不返回值</b>：Hub 方法有返回值时，客户端要等 invoke 的 Promise 兑现；
/// 而「订阅成功」这件事本身没有数据要取，返回 <c>void</c> 更直白。
/// 失败则以 <c>OnStreamError</c> 事件推给客户端。</para>
/// </remarks>
/// <param name="Success">是否成功。</param>
/// <param name="Code">错误码；成功时为 <see langword="null"/>。</param>
/// <param name="Message">错误消息；成功时为 <see langword="null"/>。</param>
public sealed record RealtimeActionResult(bool Success, int? Code, string? Message)
{
    /// <summary>
    /// 成功结果。
    /// </summary>
    public static RealtimeActionResult Ok { get; } = new(true, null, null);

    /// <summary>
    /// 由业务异常构造失败结果。
    /// </summary>
    /// <param name="exception">业务异常。</param>
    /// <returns>失败结果。</returns>
    public static RealtimeActionResult FromError(BusinessException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new RealtimeActionResult(false, exception.Code, exception.Message);
    }
}

/// <summary>
/// 推送给客户端的实时错误。
/// </summary>
/// <param name="Code">业务错误码。</param>
/// <param name="Message">可展示的消息。</param>
public sealed record RealtimeErrorPayload(int Code, string Message);

/// <summary>
/// 一批容器日志行。
/// </summary>
/// <remarks>
/// <para><b>为什么是数组而不是单行</b>：逐行推送在高频日志下会把连接变成「每秒几百帧」，
/// 而浏览器端的渲染成本与帧数成正比。攒批把帧数压到「行数 / 批量」量级。</para>
/// <para><b>序号缺口即「丢过行」</b>：服务端频道满了会丢最旧的行（而不是阻塞推送），
/// 丢掉的行<b>已经占用了序号</b>，因此下一批的首行序号会跳号。
/// 客户端据此提示「已丢弃 N 行」——不静默丢，也不假装没发生。</para>
/// </remarks>
/// <param name="ContainerId">容器标识。</param>
/// <param name="Lines">本批日志行。</param>
public sealed record ContainerLogPayload(string ContainerId, IReadOnlyList<ContainerLogLine> Lines);

/// <summary>
/// 一次容器资源采样。
/// </summary>
/// <param name="ContainerId">容器标识。</param>
/// <param name="Stats">采样内容。</param>
public sealed record ContainerStatsPayload(string ContainerId, ContainerStatsSample Stats);
