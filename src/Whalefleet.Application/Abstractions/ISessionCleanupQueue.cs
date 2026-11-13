using System.Diagnostics.CodeAnalysis;

namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 过期会话清理队列（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么要有它</b>：会话过期后，那行数据除了占地方没有别的用处。
/// 但清理动作<b>不能</b>放在请求路径里同步做——一次清理失败不该拖慢一次普通请求。
/// 因此请求路径只「投递」，删除由后台工作器做。</para>
/// <para><b>投递失败是可以接受的</b>：队列满时丢弃，另有每小时一次的全量兜底清理补上。
/// 阻塞反而会让「某个会话过期」把请求线程卡住。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Queue 是本项目的领域名词（投递队列），非 CA1711 所针对的系统命名空间后缀。")]
public interface ISessionCleanupQueue
{
    /// <summary>
    /// 投递一个待清理的会话 id。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <returns>成功入队返回 <see langword="true"/>；队列已满返回 <see langword="false"/>。</returns>
    bool TryEnqueue(Guid sessionId);
}
