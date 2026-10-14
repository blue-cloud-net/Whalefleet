namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 带"删除人"审计的对象：在 <see cref="IHasDeletionTime"/> 基础上补充删除者标识。
/// </summary>
/// <typeparam name="TUserKey">删除者主键类型，通常与 <see cref="ICreationAuditedObject{TUserKey}"/> 保持一致。</typeparam>
/// <remarks>
/// 约定：<c>DeleterId</c> 仅在 <c>DeletionTime</c> 非空时才有意义；软删除被撤销（Restore）后应复位为
/// <c>default(TUserKey)</c>。
/// </remarks>
public interface IDeletionAuditedObject<TUserKey> : IHasDeletionTime
{
    /// <summary>
    /// 删除者标识；<c>default(TUserKey)</c> 表示系统操作（无登录用户）。
    /// </summary>
    TUserKey DeleterId { get; }
}
