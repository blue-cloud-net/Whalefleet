namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 一行容器日志。
/// </summary>
/// <remarks>
/// <para><b>为什么行号由本项目生成</b>：Docker 的 logs 接口返回的是原始字节流，
/// 既没有行号也没有游标——能当「位置」用的只有行内容本身与每行的时间戳前缀。
/// 而行号是排查时最有用的东西（「第 4231 行报错了」比「往上翻 40 屏」有用得多），
/// 因此这里自己编一份。</para>
/// <para><b>序号语义</b>：同一次订阅内从 1 开始单调递增；
/// 客户端首屏用历史接口拿到 N 行后，订阅时从 N+1 继续。
/// <b>出现缺口即表示中间的行被丢弃了</b>（服务端频道溢出时的 DropOldest），
/// 前端据此提示「已丢弃 N 行」——不静默丢，也不假装没发生。</para>
/// </remarks>
/// <param name="Seq">行序号（同一次订阅内从 1 起单调递增）。</param>
/// <param name="Timestamp">该行的时间戳（UTC）；守护进程未提供时为 <see langword="null"/>。</param>
/// <param name="Line">日志正文（<b>不含</b>时间戳前缀）。</param>
public sealed record ContainerLogLine(int Seq, DateTimeOffset? Timestamp, string Line);
