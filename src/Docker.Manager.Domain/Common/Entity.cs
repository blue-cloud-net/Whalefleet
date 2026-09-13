namespace Docker.Manager.Domain.Common;

/// <summary>
/// 实体基类。所有领域实体（聚合根、聚合成员）继承本类。
/// </summary>
/// <remarks>
/// <para>实体与值对象的区别：实体有"身份"（<see cref="Id"/>），生命周期内 Id 不变；
/// 值对象无身份，由属性值定义相等性。</para>
/// <para>领域事件通过 <see cref="AddDomainEvent"/> 暂存，提交后由仓储或工作单元统一派发。</para>
/// </remarks>
/// <typeparam name="TId">主键类型（推荐 <see cref="Guid"/> 或强类型 ID）。</typeparam>
public abstract class Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>
    /// 实体主键。
    /// </summary>
    public TId Id { get; protected set; } = default!;

    /// <summary>
    /// 实体内产生的领域事件（只读视图）。
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// 添加一个领域事件。
    /// </summary>
    /// <param name="event">领域事件实例（不能为空）。</param>
    public void AddDomainEvent(IDomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _domainEvents.Add(@event);
    }

    /// <summary>
    /// 清空已派发的领域事件。仓储 / 工作单元在持久化后调用。
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// 实体相等性按 Id 判定。两个不同实例但 Id 相同视为同一实体。
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (GetType() != other.GetType())
        {
            return false;
        }

        if (Id is null || other.Id is null)
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    /// <summary>
    /// 哈希码以 Id 为基础。
    /// </summary>
    public override int GetHashCode() => Id is null ? 0 : EqualityComparer<TId>.Default.GetHashCode(Id);

    /// <summary>
    /// 由派生类实现：判定业务层面的等价性（用于跨类型业务比较，例如不同命名空间但同语义的实体）。
    /// </summary>
    /// <remarks>默认实现即 <see cref="Equals(object?)"/>，大多数场景无需重写。</remarks>
    protected virtual bool EntityEquals(Entity<TId> other) => Equals(other);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}