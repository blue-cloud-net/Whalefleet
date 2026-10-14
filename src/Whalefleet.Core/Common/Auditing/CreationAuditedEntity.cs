namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 仅带创建审计的实体基类：记录<b>何时</b>创建、<b>谁</b>创建。
/// </summary>
/// <typeparam name="TId">实体主键类型。</typeparam>
/// <typeparam name="TUserKey">审计人主键类型。</typeparam>
/// <remarks>
/// <para><b>写入方式</b>：审计属性为 <c>internal set</c>，外部（含应用层）<b>只读</b>；
/// 唯一写入入口是 <see cref="MarkCreated"/>。仓储基类在插入时调用它，
/// 把 <c>TimeProvider.GetUtcNow().UtcDateTime</c> 与当前用户 ID 灌入（0.0.3 落地）。</para>
/// <para><b>为什么方法要显式接收时间与操作人</b>：实体不应自行取当前时间，
/// 否则难以测试、且无法从数据库复原既有值。<see cref="MarkCreated"/> 因此<b>覆盖式</b>赋值，
/// 既可用于"新建时写入"，也可用于"从库中读出后按原值复原"。</para>
/// <para>需要修改审计时改用 <see cref="AuditedEntity{TId, TUserKey}"/>；
/// 需要软删除时改用 <see cref="FullAuditedEntity{TId, TUserKey}"/>。</para>
/// </remarks>
public abstract class CreationAuditedEntity<TId, TUserKey> : Entity<TId>, ICreationAuditedObject<TUserKey>
    where TId : notnull
{
    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    public DateTime CreationTime { get; internal set; }

    /// <summary>
    /// 创建者标识；<c>default(TUserKey)</c> 表示系统操作（无登录用户）。
    /// </summary>
    public TUserKey CreatorId { get; internal set; } = default!;

    /// <summary>
    /// 标记创建审计信息。<b>覆盖式</b>赋值：重复调用以最后一次为准。
    /// </summary>
    /// <param name="creationTime">创建时间，必须为 UTC。</param>
    /// <param name="creatorId">创建者标识；传 <c>default(TUserKey)</c> 表示系统操作。</param>
    public void MarkCreated(DateTime creationTime, TUserKey creatorId)
    {
        this.CreationTime = creationTime;
        this.CreatorId = creatorId;
    }
}
