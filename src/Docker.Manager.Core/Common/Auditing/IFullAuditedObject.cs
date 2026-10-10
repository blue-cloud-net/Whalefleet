namespace Docker.Manager.Core.Common.Auditing;

/// <summary>
/// 全量审计对象：创建、最后修改、软删除三段审计齐全，且支持软删除。
/// </summary>
/// <typeparam name="TUserKey">审计人主键类型。</typeparam>
/// <remarks>
/// 需要"记录谁删的"时用它；只需标记删除与否时用 <see cref="ISoftDelete"/>；
/// 需要"删的时间"但不需要"谁删的"时用 <see cref="IHasDeletionTime"/>。
/// </remarks>
public interface IFullAuditedObject<TUserKey> :
    IAuditedObject<TUserKey>,
    IDeletionAuditedObject<TUserKey>;
