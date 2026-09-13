using System;

namespace Docker.Manager.Domain.Common;

/// <summary>
/// 领域事件标记接口。
/// </summary>
/// <remarks>
/// <para>领域事件表示领域中已发生的事实，由聚合根在状态变更时产生。
/// 命名约定：过去时态（如 <c>ContainerStarted</c>、<c>HostRegistered</c>）。</para>
/// <para>处理方式：跨聚合修改通过订阅领域事件实现最终一致性；同聚合内修改直接调用方法。</para>
/// </remarks>
public interface IDomainEvent
{
    /// <summary>
    /// 事件发生时间（UTC）。
    /// </summary>
    DateTimeOffset OccurredAt { get; }
}