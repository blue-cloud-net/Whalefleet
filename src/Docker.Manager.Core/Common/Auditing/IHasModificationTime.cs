namespace Docker.Manager.Core.Common.Auditing;

/// <summary>
/// 拥有最后修改时间的对象。只声明契约，不提供实现，供任意实体按需混入。
/// </summary>
/// <remarks>
/// <para>时间一律为 <b>UTC</b>。<see langword="null"/> 表示<b>创建后从未被修改过</b>，
/// 与"修改时间是默认值"是两件事，消费方（如"最近更新列表"排序）应显式处理 <see langword="null"/>。</para>
/// </remarks>
public interface IHasModificationTime
{
    /// <summary>
    /// 最后修改时间（UTC）；从未修改过为 <see langword="null"/>。
    /// </summary>
    DateTime? LastModificationTime { get; }
}
