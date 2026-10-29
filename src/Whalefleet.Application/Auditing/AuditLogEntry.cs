using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Auditing;

/// <summary>
/// 审计日志的对外投影（一行）。
/// </summary>
/// <param name="Id">主键。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
/// <param name="Action">动作标识（前端用 Record 映射成文案，未命中回退原值）。</param>
/// <param name="ActorName">操作者名（快照）。</param>
/// <param name="TargetType">目标类型；无目标为 <see langword="null"/>。</param>
/// <param name="TargetName">目标名（快照）；无目标为 <see langword="null"/>。</param>
/// <param name="HostName">主机名（快照）；全局操作为 <see langword="null"/>。</param>
/// <param name="Outcome">结果：<c>success</c> / <c>denied</c> / <c>failure</c>。</param>
/// <param name="ErrorCode">失败时的业务错误码。</param>
/// <param name="ClientIp">来源 IP。</param>
/// <param name="Detail">附加说明。</param>
/// <remarks>
/// <para><b>为什么不直接返回实体</b>：这是对外契约，实体字段一变就会破坏前端；
/// 而且实体带 <c>HostId</c> / <c>ActorId</c> 这类内部主键，界面用不上。</para>
/// <para><b>为什么没有 <c>Category</c></b>：两个页面各查一类，返回体里的类别是常量，
/// 给出来只会多一个「可能与请求不一致」的字段。</para>
/// </remarks>
public sealed record AuditLogEntry(
    Guid Id,
    DateTime OccurredAt,
    string Action,
    string ActorName,
    string? TargetType,
    string? TargetName,
    string? HostName,
    string Outcome,
    int? ErrorCode,
    string? ClientIp,
    string? Detail);

/// <summary>
/// 审计日志分页结果。
/// </summary>
/// <param name="Items">当前页记录（按时间倒序）。</param>
/// <param name="Total">满足条件的总条数。</param>
/// <param name="PageIndex">当前页码（从 0 开始）。</param>
/// <param name="PageSize">每页条数（夹紧后的实际值）。</param>
/// <remarks>
/// <b>把夹紧后的 <see cref="PageIndex"/> / <see cref="PageSize"/> 一并返回</b>：
/// 调用方传了 <c>pageSize=9999</c> 却只拿到 100 条时，能从响应里看出服务端实际用了多少。
/// </remarks>
public sealed record AuditLogListResult(
    IReadOnlyList<AuditLogEntry> Items,
    int Total,
    int PageIndex,
    int PageSize);
