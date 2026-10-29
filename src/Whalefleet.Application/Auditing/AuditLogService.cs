using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Auditing;

/// <summary>
/// 审计日志查询与导出用例。
/// </summary>
/// <remarks>
/// <para><b>为什么只读</b>：审计表不提供修改与删除——接口层没有入口，比在实现里加判断更可靠。</para>
/// <para><b>为什么导出要单独一条路径</b>：导出会绕过分页，因此必须先<b>计数</b>再决定要不要给。
/// 直接按上限截断会让用户拿到一份「看起来完整、其实缺了后半段」的证据文件，
/// 那比拒绝导出更糟——它不会报错。</para>
/// </remarks>
public sealed class AuditLogService
{
    /// <summary>默认每页条数。</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（与其它列表端点一致）。</summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// 单次导出的最大行数。
    /// </summary>
    /// <remarks>
    /// 5 万行的 CSV 约 10–20 MB，已远超「人工核对」的规模；再大应当缩小时间范围。
    /// 超过上限时**拒绝**而不是截断——见类型 remarks。
    /// </remarks>
    public const int MaxExportRows = 50_000;

    private readonly IAuditLogRepository _repository;
    private readonly IAuditLogger _auditLogger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 初始化审计查询用例。
    /// </summary>
    /// <param name="repository">审计仓储。</param>
    /// <param name="auditLogger">审计写入（导出本身也要留痕）。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AuditLogService(
        IAuditLogRepository repository,
        IAuditLogger auditLogger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(auditLogger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repository = repository;
        _auditLogger = auditLogger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 分页查询审计日志。
    /// </summary>
    /// <param name="category">类别。</param>
    /// <param name="request">查询请求。</param>
    /// <param name="hostId">固定的主机作用域（主机侧页面用）；全局页面传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分页结果。</returns>
    /// <exception cref="BusinessException">请求参数非法（<c>1000</c>）。</exception>
    public async Task<AuditLogListResult> ListAsync(
        AuditCategory category,
        ListAuditLogsRequest request,
        Guid? hostId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var filter = AuditLogFilterBuilder.Build(category, request, hostId, now);

        var pageIndex = Math.Max(0, request.PageIndex ?? 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        var total = await _repository.CountAsync(filter, cancellationToken).ConfigureAwait(false);

        var items = total == 0
            ? []
            : await _repository
                .ListAsync(filter, pageIndex * pageSize, pageSize, cancellationToken)
                .ConfigureAwait(false);

        return new AuditLogListResult(
            [.. items.Select(ToEntry)],
            total,
            pageIndex,
            pageSize);
    }

    /// <summary>
    /// 查询要导出的记录（不写响应体，便于单测）。
    /// </summary>
    /// <param name="category">类别。</param>
    /// <param name="request">查询请求。</param>
    /// <param name="hostId">固定的主机作用域。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待导出的记录（按时间倒序）。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>），或结果集超过 <see cref="MaxExportRows"/>（<c>1000</c>）。
    /// </exception>
    /// <remarks>
    /// 导出成功后写一条 <c>audit.export</c>：**导出本身是最容易把整份审计悄悄带走**的动作，
    /// 不留痕等于审计可以被绕开。
    /// </remarks>
    public async Task<IReadOnlyList<AuditLog>> ExportAsync(
        AuditCategory category,
        ListAuditLogsRequest request,
        Guid? hostId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var filter = AuditLogFilterBuilder.Build(category, request, hostId, now);

        var total = await _repository.CountAsync(filter, cancellationToken).ConfigureAwait(false);

        if (total > MaxExportRows)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"符合条件的记录有 {total} 条，超过单次导出上限 {MaxExportRows} 条，请缩小时间范围后再导出。");
        }

        var rows = await _repository
            .ListAsync(filter, 0, MaxExportRows, cancellationToken)
            .ConfigureAwait(false);

        await _auditLogger
            .RecordAsync(
                new AuditLogDraft
                {
                    Category = AuditCategory.Activity,
                    Action = AuditActions.AuditExport,
                    Outcome = AuditOutcome.Success,
                    TargetType = AuditTargetTypes.Audit,
                    TargetName = category == AuditCategory.Authentication ? "authentication" : "activity",
                    Detail = $"导出 {rows.Count} 行",
                },
                cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }

    /// <summary>
    /// 实体 → 对外投影。
    /// </summary>
    private static AuditLogEntry ToEntry(AuditLog log) =>
        new(
            log.Id,
            log.OccurredAt,
            log.Action,
            log.ActorName,
            log.TargetType,
            log.TargetName,
            log.HostName,
            AuditLogFilterBuilder.ToText(log.Outcome),
            log.ErrorCode,
            log.ClientIp,
            log.Detail);
}
