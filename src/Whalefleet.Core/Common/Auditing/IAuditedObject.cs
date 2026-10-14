namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 完整审计对象：同时记录<b>创建</b>与<b>最后修改</b>。最常用的审计切片。
/// </summary>
/// <typeparam name="TUserKey">审计人主键类型。</typeparam>
/// <remarks>
/// 约定：<c>LastModificationTime</c> 为 <see langword="null"/> ⇔ 该实体<b>从未被修改过</b>；
/// 此刻 <c>LastModifierId</c> 无实际意义，视为 <c>default(TUserKey)</c>。
/// </remarks>
public interface IAuditedObject<TUserKey> :
    ICreationAuditedObject<TUserKey>,
    IModificationAuditedObject<TUserKey>;
