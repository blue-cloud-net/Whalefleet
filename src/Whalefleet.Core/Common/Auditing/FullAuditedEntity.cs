namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 全量审计 + 软删除的实体基类：在 <see cref="AuditedEntity{TId, TUserKey}"/> 基础上
/// 追加"是否被软删除、何时删的、谁删的"，并提供 <see cref="Delete"/> / <see cref="Restore"/> 行为。
/// </summary>
/// <typeparam name="TId">实体主键类型。</typeparam>
/// <typeparam name="TUserKey">审计人主键类型。</typeparam>
/// <remarks>
/// <para><b>不变量</b>：<see cref="DeletionTime"/> 为 <see langword="null"/> ⇔
/// <see cref="IsDeleted"/> 为 <see langword="false"/>。两个方法成对维护该不变式，
/// 外部无法绕过（审计属性为 <c>internal set</c>）。</para>
/// <para><b>查询约定</b>（0.0.3 落地）：仓储生成的 <c>SELECT</c> 默认追加
/// <c>WHERE is_deleted = 0</c>，软删除数据对业务查询不可见；
/// 仅在显式传入"包含已删除"开关时才放开，供回收站 / 数据修复类用例使用。</para>
/// <para><b>为什么不做参数校验</b>：重复 <c>Delete()</c> 或对未删除实体 <c>Restore()</c> 均为幂等，
/// 不应抛异常打断调用方流程。</para>
/// </remarks>
public abstract class FullAuditedEntity<TId, TUserKey> :
    AuditedEntity<TId, TUserKey>,
    IFullAuditedObject<TUserKey>
    where TId : notnull
{
    /// <summary>
    /// 是否已被软删除。默认 <see langword="false"/>。
    /// </summary>
    public bool IsDeleted { get; internal set; }

    /// <summary>
    /// 软删除时间（UTC）；未被删除为 <see langword="null"/>。
    /// </summary>
    public DateTime? DeletionTime { get; internal set; }

    /// <summary>
    /// 删除者标识；<c>default(TUserKey)</c> 表示系统操作（无登录用户）。
    /// </summary>
    public TUserKey DeleterId { get; internal set; } = default!;

    /// <summary>
    /// 标记软删除。幂等：重复调用会刷新删除时间与删除者。
    /// </summary>
    /// <param name="deletionTime">删除时间，必须为 UTC。</param>
    /// <param name="deleterId">删除者标识；传 <c>default(TUserKey)</c> 表示系统操作。</param>
    public void Delete(DateTime deletionTime, TUserKey deleterId)
    {
        this.IsDeleted = true;
        this.DeletionTime = deletionTime;
        this.DeleterId = deleterId;
    }

    /// <summary>
    /// 撤销软删除（恢复）。幂等；同时复位删除时间与删除者。
    /// </summary>
    public void Restore()
    {
        this.IsDeleted = false;
        this.DeletionTime = null;
        this.DeleterId = default!;
    }
}
