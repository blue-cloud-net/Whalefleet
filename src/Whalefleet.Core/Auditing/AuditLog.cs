using Whalefleet.Core.Common;

namespace Whalefleet.Core.Auditing;

/// <summary>
/// 一条审计日志（<b>只追加、不修改、不删除</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么只派生 <see cref="Entity{TId}"/> 而不是 <c>FullAuditedEntity</c></b>：
/// 审计日志自己不需要「谁创建了这条审计」——那是无穷倒退。
/// 它也没有软删除：记录一旦写出就是事实，不提供任何删除入口。</para>
/// <para><b>为什么有两个时间</b>：只有 <see cref="OccurredAt"/>（业务发生时刻）。
/// 刻意不加「写入时刻」——两者相差只有毫秒级，而多一个时间字段会让
/// 「按哪个排序」变成第一个需要回答的问题。</para>
/// <para><b>名字是快照，不是外键</b>：<see cref="ActorName"/> / <see cref="TargetName"/> /
/// <see cref="HostName"/> 都存**当时的文本**。理由见各属性说明——
/// 一句话：被操作的对象往往会被删掉，而审计必须在那之后仍然可读。</para>
/// </remarks>
public sealed class AuditLog : Entity<Guid>
{
    /// <summary>
    /// 匿名操作者名（登录失败时可能连用户名都没给）。
    /// </summary>
    public const string AnonymousActor = "anonymous";

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。
    /// </summary>
    /// <remarks>
    /// 业务代码请改用 <see cref="Record"/>。Dapper 能写非公开 setter（见 AGENTS.md 的
    /// 「Dapper 物化能力」一段），因此这里不需要把它们公开出去。
    /// </remarks>
    private AuditLog()
    {
    }

    /// <summary>所属类别（认证 / 活动）。</summary>
    public AuditCategory Category { get; private set; }

    /// <summary>动作标识（见 <see cref="AuditActions"/>）。</summary>
    public string Action { get; private set; } = String.Empty;

    /// <summary>操作者主键；未登录（如登录失败）时为 <see langword="null"/>。</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>
    /// 操作者名（<b>快照</b>）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不靠 <see cref="ActorId"/> 现查</b>：用户会被删除，而「三个月前是谁删了这台主机」
    /// 这个问题不该因为那个账号被删就变成空白。登录失败时这里记的是**尝试的用户名**。
    /// </remarks>
    public string ActorName { get; private set; } = String.Empty;

    /// <summary>目标类型（见 <see cref="AuditTargetTypes"/>）；无目标时为 <see langword="null"/>。</summary>
    public string? TargetType { get; private set; }

    /// <summary>目标主键（容器 ID / 用户 ID 等）；无目标时为 <see langword="null"/>。</summary>
    public string? TargetId { get; private set; }

    /// <summary>
    /// 目标名（<b>快照</b>）。
    /// </summary>
    /// <remarks>
    /// <b>这是「容器被删除后，历史审计仍能显示当时的容器名」的实现方式</b>：
    /// 名字在写入那一刻就固化，之后不再跟随资源变化。
    /// 只存 ID 是不够的——删掉的容器已经查不到名字了。
    /// </remarks>
    public string? TargetName { get; private set; }

    /// <summary>作用域主机主键（<c>hosts.id</c>，Guid）；全局操作为 <see langword="null"/>。</summary>
    public Guid? HostId { get; private set; }

    /// <summary>作用域主机名（<b>快照</b>；主机名可改，审计要保留当时的叫法）。</summary>
    public string? HostName { get; private set; }

    /// <summary>结果。</summary>
    public AuditOutcome Outcome { get; private set; }

    /// <summary>失败时的业务错误码；成功为 <see langword="null"/>。</summary>
    public int? ErrorCode { get; private set; }

    /// <summary>来源 IP（经反向代理头修正后的客户端地址）；后端内部调用为 <see langword="null"/>。</summary>
    public string? ClientIp { get; private set; }

    /// <summary>
    /// 附加说明（自由文本，界面上折叠显示）。
    /// </summary>
    /// <remarks>
    /// <b>绝不写入密码、令牌、环境变量值</b>——审计日志的可见范围比业务数据宽得多。
    /// 允许放的是「会话 ID / 耗时 / 退出码 / 变更了哪些字段」这类排障信息。
    /// </remarks>
    public string? Detail { get; private set; }

    /// <summary>发生时刻（UTC）。</summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>
    /// 由草稿创建一条审计记录。
    /// </summary>
    /// <param name="id">主键。</param>
    /// <param name="draft">草稿（各字段的原始值）。</param>
    /// <returns>新的审计记录。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">
    /// 动作标识为空、操作者名为空，或时间不是 UTC。
    /// </exception>
    /// <remarks>
    /// <b>为什么时间必须是 UTC</b>：库里的时间是文本，排序与范围过滤都按字符串比较。
    /// 混入本地时间会让「最近 7 天」这类查询在跨时区部署下静默出错，且看不出来。
    /// </remarks>
    public static AuditLog Record(Guid id, AuditLogDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ActorName);

        if (draft.OccurredAt.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("审计时间必须是 UTC。", nameof(draft));
        }

        return new AuditLog
        {
            Id = id,
            Category = draft.Category,
            Action = draft.Action.Trim(),
            ActorId = draft.ActorId,
            ActorName = draft.ActorName.Trim(),
            TargetType = draft.TargetType,
            TargetId = draft.TargetId,
            TargetName = draft.TargetName,
            HostId = draft.HostId,
            HostName = draft.HostName,
            Outcome = draft.Outcome,
            ErrorCode = draft.ErrorCode,
            ClientIp = draft.ClientIp,
            Detail = draft.Detail,
            OccurredAt = draft.OccurredAt,
        };
    }
}

/// <summary>
/// 审计记录的草稿（写入前）。
/// </summary>
/// <remarks>
/// <para><b>为什么不直接 new 实体</b>：实体只提供「校验后的构造」，
/// 而写入侧需要先拼装十几个字段、再由调用方补默认值（操作者、时间、IP）。
/// 草稿把这个拼装过程与实体的不变量检查分开，实体因此永远处于合法状态。</para>
/// <para><b>为什么用 <c>init</c> 而不是构造参数</b>：十五个字段全位置化后，
/// 调用点会变成一串看不出含义的字面量。对象初始化器让每个值都带着名字。</para>
/// </remarks>
public sealed record AuditLogDraft
{
    /// <summary>所属类别。</summary>
    public required AuditCategory Category { get; init; }

    /// <summary>动作标识（见 <see cref="AuditActions"/>）。</summary>
    public required string Action { get; init; }

    /// <summary>结果。</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>发生时刻（必须 UTC；省略时由写入侧填当前时间）。</summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>操作者主键。</summary>
    public Guid? ActorId { get; init; }

    /// <summary>操作者名（快照）；省略时由写入侧取当前用户名，取不到则用 <see cref="AuditLog.AnonymousActor"/>。</summary>
    public string ActorName { get; init; } = String.Empty;

    /// <summary>目标类型。</summary>
    public string? TargetType { get; init; }

    /// <summary>目标主键。</summary>
    public string? TargetId { get; init; }

    /// <summary>目标名（快照）。</summary>
    public string? TargetName { get; init; }

    /// <summary>作用域主机主键。</summary>
    public Guid? HostId { get; init; }

    /// <summary>作用域主机名（快照）。</summary>
    public string? HostName { get; init; }

    /// <summary>失败时的业务错误码。</summary>
    public int? ErrorCode { get; init; }

    /// <summary>来源 IP。</summary>
    public string? ClientIp { get; init; }

    /// <summary>附加说明（不得含密码 / 令牌 / 环境变量值）。</summary>
    public string? Detail { get; init; }
}
