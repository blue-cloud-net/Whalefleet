using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Abstractions.Auditing;

/// <summary>
/// 审计日志写入（<b>best-effort</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么是 best-effort</b>：审计写入失败<b>不</b>应让业务操作失败——
/// 磁盘满的时候「启动容器」还是应该能启动。实现里吞掉异常并降级为 Error 日志，
/// 因此调用方不需要 <c>try/catch</c>。</para>
/// <para><b>为什么不是「失败了就回滚业务」</b>：那会把审计表变成整个系统的单点故障。
/// 权衡后的口径是：**宁可少一条审计，也不要因为审计写不进去而让运维点不动任何按钮**；
/// 写入失败的事实本身会进服务端 Error 日志，属于需要人介入的信号。</para>
/// <para><b>为什么调用方只给草稿</b>：操作者、时间、来源 IP 的默认值由实现统一填充，
/// 十五个字段里只有「业务真正知道的那些」（动作、目标、结果）需要调用方提供。
/// 这样埋点就是一行，不会因为要凑参数而被随手漏掉几个字段。</para>
/// </remarks>
public interface IAuditLogger
{
    /// <summary>
    /// 记录一条审计。
    /// </summary>
    /// <param name="draft">审计草稿。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。<b>不会因写入失败而抛出</b>。</returns>
    /// <remarks>
    /// <b>取消令牌会被忽略</b>：请求被中止（客户端断开）时，审计恰恰更需要写下来。
    /// 实现内部统一用 <see cref="CancellationToken.None"/> 写库。
    /// </remarks>
    Task RecordAsync(AuditLogDraft draft, CancellationToken cancellationToken = default);
}
