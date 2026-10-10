namespace Docker.Manager.Core.Common.Auditing;

/// <summary>
/// 拥有软删除时间的对象。在 <see cref="ISoftDelete"/> 的基础上补充"何时被删"。
/// </summary>
/// <remarks>
/// 约定：<c>DeletionTime</c> 为 <see langword="null"/> ⇔ <c>IsDeleted == false</c>。
/// 二者必须同时变化（见 <see cref="FullAuditedEntity{TId, TUserKey}.Delete"/> 与
/// <see cref="FullAuditedEntity{TId, TUserKey}.Restore"/>）。
/// </remarks>
public interface IHasDeletionTime : ISoftDelete
{
    /// <summary>
    /// 软删除时间（UTC）；未被删除为 <see langword="null"/>。
    /// </summary>
    DateTime? DeletionTime { get; }
}
