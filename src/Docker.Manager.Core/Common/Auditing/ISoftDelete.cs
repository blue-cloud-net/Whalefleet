namespace Docker.Manager.Core.Common.Auditing;

/// <summary>
/// 支持软删除的对象。只声明契约，不提供实现，供任意实体按需混入。
/// </summary>
/// <remarks>
/// <para>实现本接口即意味着该实体<b>不从数据表中物理移除</b>，而是通过
/// <see cref="IsDeleted"/> 标记。配套的<b>查询约定</b>（0.0.3 落地）：仓储生成的
/// <c>SELECT</c> 默认追加 <c>WHERE is_deleted = 0</c>；仅在显式传入"包含已删除"开关时才放开。</para>
/// <para>需要连删除人与删除时间一起记录时，改用 <see cref="IHasDeletionTime"/>。</para>
/// </remarks>
public interface ISoftDelete
{
    /// <summary>
    /// 是否已被软删除。默认 <see langword="false"/>。
    /// </summary>
    bool IsDeleted { get; }
}
