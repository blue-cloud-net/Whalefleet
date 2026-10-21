namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 容器日志流的选项。
/// </summary>
/// <remarks>
/// <para><b>为什么有 <see cref="StartSeq"/></b>：客户端首屏用历史接口拿到 N 行，
/// 订阅时要从 N+1 接着编号，这样<b>历史与实时共用一套行号</b>——
/// 用户看到的「第 4231 行」在两边指的是同一行。</para>
/// <para><b>为什么 <see cref="Since"/> 与 <see cref="StartSeq"/> 并存</b>：
/// 行号保证「显示」连续，时间戳保证「内容」不重复（跳过已读过的部分）。
/// 两者各管一件事，缺一不可：只有行号会重复推已读过的行，
/// 只有时间戳则可能在时间戳精度不足时漏行。</para>
/// </remarks>
public sealed record ContainerLogStreamOptions
{
    /// <summary>
    /// 起始行序号（同一次订阅内从该值开始单调递增）。
    /// </summary>
    public int StartSeq { get; init; } = 1;

    /// <summary>
    /// 起始时刻（Unix 秒）；只产出该时刻之后的日志。
    /// </summary>
    /// <remarks>
    /// 为 <see langword="null"/> 时只产出<b>新产生</b>的日志（等价 Docker 的 <c>tail=0</c>）。
    /// </remarks>
    public long? Since { get; init; }

    /// <summary>
    /// 是否包含 stderr；省略时包含。
    /// </summary>
    public bool IncludeStderr { get; init; } = true;
}
