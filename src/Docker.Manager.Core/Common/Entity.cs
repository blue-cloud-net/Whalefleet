namespace Docker.Manager.Core.Common;

/// <summary>
/// 实体基类。有身份、需要承载行为的业务对象继承本类。
/// </summary>
/// <remarks>
/// <para><b>充血模型约定</b>：实体应把<b>行为与不变量放在自身方法内</b>，
/// 而不是仅作为数据袋、把所有判断都推到应用服务里。典型形态是「私有构造 + 静态工厂」——
/// 工厂方法在创建时校验不变量，之后对象在生命周期内始终处于合法状态。</para>
/// <para>实体与值对象的区别：实体有"身份"（<see cref="Id"/>），生命周期内 Id 不变；
/// 值对象无身份，由属性值定义相等性。</para>
/// <para><b>不变量违反时抛什么</b>：本项目不定义领域异常，使用 BCL 的
/// <see cref="ArgumentException"/>（含 <see cref="ArgumentNullException"/>）表达参数与不变量违规，
/// 全局异常处理中间件会将其映射为 <c>1000</c> / HTTP 400。</para>
/// </remarks>
/// <typeparam name="TId">主键类型（推荐 <see cref="Guid"/> 或强类型 ID）。</typeparam>
public abstract class Entity<TId>
    where TId : notnull
{
    /// <summary>
    /// 实体主键。
    /// </summary>
    public TId Id { get; protected set; } = default!;

    /// <summary>
    /// 实体相等性按 Id 判定。两个不同实例但 Id 相同视为同一实体。
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (this.GetType() != other.GetType())
            return false;

        return EqualityComparer<TId>.Default.Equals(this.Id, other.Id);
    }

    /// <summary>
    /// 哈希码以 Id 为基础。
    /// </summary>
    public override int GetHashCode() => this.Id is null ? 0 : EqualityComparer<TId>.Default.GetHashCode(this.Id);

    /// <summary>
    /// 由派生类实现：判定业务层面的等价性（用于跨类型业务比较，例如不同命名空间但同语义的实体）。
    /// </summary>
    /// <remarks>默认实现即 <see cref="Equals(global::System.Object?)"/>，大多数场景无需重写。</remarks>
    protected virtual bool EntityEquals(Entity<TId> other) => this.Equals(other);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
