namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 拥有创建时间的对象。只声明契约，不提供实现，供任意实体按需混入。
/// </summary>
/// <remarks>
/// <para>本接口是审计体系的<b>最细粒度切片</b>：只想记录"何时创建"、不想记录"谁创建"时，
/// 实现本接口即可，不必牵连 <see cref="ICreationAuditedObject{TUserKey}"/>。</para>
/// <para>时间一律为 <b>UTC</b>。写入方在 0.0.3 由仓储基类统一从
/// <c>TimeProvider.GetUtcNow().UtcDateTime</c> 取值传入（见
/// <see cref="CreationAuditedEntity{TId, TUserKey}.MarkCreated"/>）。</para>
/// </remarks>
public interface IHasCreationTime
{
    /// <summary>
    /// 创建时间（UTC）。实体一经创建不再变更。
    /// </summary>
    DateTime CreationTime { get; }
}
