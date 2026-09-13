namespace Docker.Manager.Domain.Common;

/// <summary>
/// 聚合根基类。继承 <see cref="Entity{TId}"/> 并实现 <see cref="IAggregateRoot{TId}"/>。
/// </summary>
/// <remarks>
/// <para>所有聚合根继承本类。除非有特殊理由（例如需要把聚合根与普通实体完全分离），
/// 否则应使用本基类而非直接继承 <see cref="Entity{TId}"/>。</para>
/// </remarks>
/// <typeparam name="TId">主键类型。</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot<TId>
    where TId : notnull
{
    /// <summary>
    /// 显式实现接口，保证 <see cref="IAggregateRoot{TId}"/> 与 <see cref="Entity{TId}"/> 行为一致。
    /// </summary>
    TId IAggregateRoot<TId>.Id => Id;

    IReadOnlyCollection<IDomainEvent> IAggregateRoot<TId>.DomainEvents => DomainEvents;

    void IAggregateRoot<TId>.AddDomainEvent(IDomainEvent domainEvent) => AddDomainEvent(domainEvent);

    void IAggregateRoot<TId>.ClearDomainEvents() => ClearDomainEvents();
}