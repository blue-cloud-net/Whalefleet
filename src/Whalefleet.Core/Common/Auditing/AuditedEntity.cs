namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 带创建 + 修改审计的实体基类：在 <see cref="CreationAuditedEntity{TId, TUserKey}"/> 基础上
/// 追加"何时被最后修改、谁改的"。
/// </summary>
/// <typeparam name="TId">实体主键类型。</typeparam>
/// <typeparam name="TUserKey">审计人主键类型。</typeparam>
/// <remarks>
/// <para>新建后、首次修改前，<see cref="LastModificationTime"/> 为 <see langword="null"/>，
/// 表示<b>从未被修改过</b>；此时 <see cref="LastModifierId"/> 视为
/// <c>default(TUserKey)</c>。仓储基类在更新时调用 <see cref="MarkModified"/>。</para>
/// </remarks>
public abstract class AuditedEntity<TId, TUserKey> :
    CreationAuditedEntity<TId, TUserKey>,
    IAuditedObject<TUserKey>
    where TId : notnull
{
    /// <summary>
    /// 最后修改时间（UTC）；从未被修改过为 <see langword="null"/>。
    /// </summary>
    public DateTime? LastModificationTime { get; internal set; }

    /// <summary>
    /// 最后修改者标识；<c>default(TUserKey)</c> 表示系统操作（无登录用户）。
    /// </summary>
    public TUserKey LastModifierId { get; internal set; } = default!;

    /// <summary>
    /// 标记修改审计信息。<b>覆盖式</b>赋值：每次成功修改都应调用一次，以最后一次为准。
    /// </summary>
    /// <param name="lastModificationTime">最后修改时间，必须为 UTC。</param>
    /// <param name="lastModifierId">最后修改者标识；传 <c>default(TUserKey)</c> 表示系统操作。</param>
    public void MarkModified(DateTime lastModificationTime, TUserKey lastModifierId)
    {
        this.LastModificationTime = lastModificationTime;
        this.LastModifierId = lastModifierId;
    }
}
