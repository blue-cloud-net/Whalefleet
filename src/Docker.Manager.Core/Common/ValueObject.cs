namespace Docker.Manager.Core.Common;

/// <summary>
/// 值对象基类。无身份、由属性值定义相等性的对象继承本类。
/// </summary>
/// <remarks>
/// <para>值对象无身份（无 Id），由属性值定义相等性。
/// 实现要点：
/// - 重写 <see cref="GetEqualityComponents"/> 返回所有参与相等性比较的字段；
/// - 一旦字段集合确定，<see cref="Equals(object?)"/>、<see cref="GetHashCode"/> 自动生效。</para>
/// <para>值对象应当是<b>不可变</b>的（init-only 或 readonly 属性）。不变量校验放在构造函数里，
/// 违规时抛 <see cref="ArgumentException"/>。</para>
/// <para>适用场景：复合类型（如 <c>HostEndpoint</c>、<c>PortMapping</c>）、
/// 或语义上需要"整体比对"的枚举包装（如 <c>ContainerStatus</c>）。</para>
/// </remarks>
public abstract class ValueObject
{
    /// <summary>
    /// 返回所有参与相等性比较的字段值。顺序须稳定。
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <summary>
    /// 值对象的相等性：类型相同 + 所有 equality component 逐项相等。
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
        {
            return false;
        }

        var other = (ValueObject)obj;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    /// <summary>
    /// 哈希码基于 equality components 组合（顺序敏感）。
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}