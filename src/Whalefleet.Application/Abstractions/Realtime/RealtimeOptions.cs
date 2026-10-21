namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 实时通道（SignalR）选项（配置节 <c>Realtime</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么这些值可配</b>：它们全部是<b>成本与体验的取舍点</b>——
/// 一次订阅要占用一条到 Docker 守护进程的长连接、一个后台泵任务与一个频道槽位。
/// 默认值按「单机、几十个容器、几个人在用」的场景定，
/// 而真把它放到生产上时，运维需要有旋钮可调而不必改代码重新发布。</para>
/// <para><b>为什么全部走 <c>ValidateOnStart</c></b>：非法值（例如把批量行数配成 0）
/// 必须在启动时炸，而不是在第一条日志到达时表现为「什么都不推」——
/// 那种故障在页面上表现为「实时日志没反应」，几乎无从定位。</para>
/// </remarks>
public sealed class RealtimeOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Realtime";

    /// <summary>
    /// 单个连接在**同一类**资源上的最大订阅数（日志、统计、进度各自计数）。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有上限</b>：订阅数与后端成本近似线性相关——每个订阅占一条
    /// Docker 长连接、一个后台泵任务与一个频道槽位。没有上限时，
    /// 一个写错的客户端（循环调用 <c>SubscribeLogs</c>）就能把守护进程拖垮。
    /// <br/>
    /// 5 的依据：一个人同时盯着 5 个容器的日志是合理上限；再多应该用「切到下一个容器」代替。
    /// </remarks>
    public int MaxSubscriptionsPerKind { get; set; } = 5;

    /// <summary>
    /// 日志批次最大行数。
    /// </summary>
    /// <remarks>
    /// <b>为什么要批量</b>：逐行推送在高频日志下会把连接变成「每秒几百帧」，
    /// 而浏览器端的渲染成本与帧数成正比。攒批把帧数压到「行数 / 批量」量级。
    /// <br/>
    /// 100 的依据：一批 100 行约等于一次普通页面的 DOM 更新量，低于它收益不大，
    /// 高于它则首行出现的延迟变长（用户会以为「日志卡住了」）。
    /// </remarks>
    public int LogBatchLines { get; set; } = 100;

    /// <summary>
    /// 日志批次的最长等待时间（毫秒）。
    /// </summary>
    /// <remarks>
    /// <b>为什么与行数并存</b>：只有行数阈值时，低流量日志会一直等不到一批——
    /// 用户看到的是「日志不动了」。加上时间阈值后，即使只有一行也会在
    /// <see cref="LogFlushIntervalMs"/> 内推出去。
    /// </remarks>
    public int LogFlushIntervalMs { get; set; } = 200;

    /// <summary>
    /// 单个日志订阅的频道容量（行数）。
    /// </summary>
    /// <remarks>
    /// <b>满了会丢最旧的行</b>（DropOldest）而不是阻塞推送：推送端是 Docker 的读取回调，
    /// 阻塞它等于停掉 Docker 侧的日志读取。丢失的行在下一批里表现为<b>序号缺口</b>，
    /// 前端据此提示「已丢弃 N 行」——不静默丢，也不假装没发生。
    /// </remarks>
    public int LogChannelCapacity { get; set; } = 2000;

    /// <summary>
    /// 统计采样间隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// Docker 的 stats 流本身是 1 秒一帧；这个间隔是**服务端再采样**的节奏，
    /// 用来削掉客户端的渲染压力。取值必须 ≥ 1000，否则采到的还是同一帧。
    /// </remarks>
    public int StatsSampleIntervalMs { get; set; } = 1000;

    /// <summary>
    /// 容器事件监听的枚举间隔（秒）。
    /// </summary>
    /// <remarks>
    /// 后台服务每隔这段时间重新枚举一次主机列表，好知道有没有新主机加入、
    /// 或者某台主机被删掉了。间隔太长则新主机要等很久才有事件；
    /// 太短则数据库被反复查询。
    /// </remarks>
    public int HostEnumerationIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 容器事件流断开后的重连退避（秒）。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有退避</b>：Docker 的 events 流在守护进程重启、网络抖动后会立刻断开。
    /// 没有退避时，后台服务会以「每秒重连几十次」的频率打守护进程，
    /// 既刷日志又可能把刚起来的服务再次拖垮。
    /// </remarks>
    public int EventReconnectBackoffSeconds { get; set; } = 5;

    /// <summary>
    /// 校验配置的合法性。
    /// </summary>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 逐项检查而不是「都大于零」：不同项的合理下界不同
    /// （批量行数至少 1，采样间隔至少 1000，因为 Docker 本身就 1 秒一帧）。
    /// </remarks>
    public bool IsValid() =>
        MaxSubscriptionsPerKind > 0
        && LogBatchLines > 0
        && LogFlushIntervalMs > 0
        && LogChannelCapacity >= LogBatchLines
        && StatsSampleIntervalMs >= 1000
        && HostEnumerationIntervalSeconds > 0
        && EventReconnectBackoffSeconds > 0;
}
