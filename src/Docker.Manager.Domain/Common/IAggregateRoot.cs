namespace Docker.Manager.Domain.Common;

/// <summary>
/// 聚合根标记接口。
/// </summary>
/// <remarks>
/// <para>聚合根是聚合内对外的唯一入口。外部只能持有聚合根引用，不能直接访问聚合成员。</para>
/// <para>聚合内强一致，聚合间最终一致（通过领域事件）。</para>
/// <para>只有聚合根可以产生领域事件（继承 <see cref="Entity{TId}"/> 的 <see cref="Entity{TId}.AddDomainEvent"/>）。</para>
/// </remarks>
/// <typeparam name="TId">主键类型。</typeparam>
public interface IAggregateRoot<out TId>
    where TId : notnull
{
    /// <summary>
    /// 聚合根主键。
    /// </summary>
    TId Id { get; }

    /// <summary>
    /// 聚合产生的领域事件。
    /// </summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>
    /// 添加领域事件（仓储 / 工作单元负责派发与清空）。
    /// </summary>
    void AddDomainEvent(IDomainEvent domainEvent);

    /// <summary>
    /// 清空已派发的领域事件。
    /// </summary>
    void ClearDomainEvents();
}