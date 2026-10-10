namespace Docker.Manager.Core.Common.Auditing;

/// <summary>
/// 带"最后修改人"审计的对象：在 <see cref="IHasModificationTime"/> 基础上补充最后修改者标识。
/// </summary>
/// <typeparam name="TUserKey">修改者主键类型，通常与 <see cref="ICreationAuditedObject{TUserKey}"/> 保持一致。</typeparam>
/// <remarks>
/// 与 <see cref="ICreationAuditedObject{TUserKey}"/> 一样采用<b>默认值哨兵</b>表达"系统操作"，
/// 且 <c>LastModifierId</c> 仅在 <c>LastModificationTime</c> 非空时才有意义。
/// </remarks>
public interface IModificationAuditedObject<TUserKey> : IHasModificationTime
{
    /// <summary>
    /// 最后修改者标识；<c>default(TUserKey)</c> 表示系统操作（无登录用户）。
    /// </summary>
    TUserKey LastModifierId { get; }
}
