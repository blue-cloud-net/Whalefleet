using System.Text;
using Whalefleet.Api.Security;
using Whalefleet.Application.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 审计日志端点（<b>全局作用域</b>，需要 <c>audit.read</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么两页各一组路径而不是一个 <c>category</c> 参数</b>：两个页面的
/// 过滤维度本就不同（认证日志没有目标与主机），路径分开后前端不必每次多传一个常量，
/// 也不会出现「传错类别、页面显示另一类数据」这种整页错位的故障。</para>
/// <para><b>为什么没有删除 / 清空端点</b>：审计一旦可被删就等于没有审计。
/// 这不是「暂未实现」，是设计上的拒绝。</para>
/// <para><b>导出为什么要单独一条路径</b>：它会绕过分页，并且<b>先计数再决定给不给</b>——
/// 超过上限时拒绝，而不是截断。截断会产出一份「看起来完整、实际缺后半段」的证据文件，
/// 而且不会报错。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/logs")]
[Authorize(Policy = PermissionPolicies.AuditRead)]
public sealed class AuditLogsController : ControllerBase
{
    private readonly AuditLogService _audit;
    private readonly TimeProvider _clock;

    /// <summary>
    /// 初始化审计控制器。
    /// </summary>
    /// <param name="audit">审计查询用例。</param>
    /// <param name="clock">时间源（导出文件名用）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AuditLogsController(AuditLogService audit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// 分页查询认证日志。
    /// </summary>
    /// <param name="request">查询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分页结果。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">时间区间颠倒或结果取值不认识（<c>1000</c>）。</response>
    /// <response code="403">缺少 <c>audit.read</c>（<c>1502</c>）。</response>
    [HttpPost("authentication/list")]
    [ProducesResponseType(typeof(ApiResult<AuditLogListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAuthenticationAsync(
        [FromBody] ListAuditLogsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _audit
            .ListAsync(AuditCategory.Authentication, request, hostId: null, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 分页查询活动日志。
    /// </summary>
    /// <param name="request">查询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分页结果。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">时间区间颠倒或结果取值不认识（<c>1000</c>）。</response>
    /// <response code="403">缺少 <c>audit.read</c>（<c>1502</c>）。</response>
    [HttpPost("activity/list")]
    [ProducesResponseType(typeof(ApiResult<AuditLogListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListActivityAsync(
        [FromBody] ListAuditLogsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _audit
            .ListAsync(AuditCategory.Activity, request, hostId: null, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 导出认证日志（CSV 流）。
    /// </summary>
    /// <param name="request">查询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>CSV 文件流。</returns>
    /// <response code="200">导出流已开始。</response>
    /// <response code="400">参数非法，或结果超过导出上限（<c>1000</c>，此时返回 JSON 而非 CSV）。</response>
    /// <response code="403">缺少 <c>audit.read</c>（<c>1502</c>）。</response>
    [HttpPost("authentication/export")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public Task ExportAuthenticationAsync(
        [FromBody] ListAuditLogsRequest request,
        CancellationToken cancellationToken) =>
        this.ExportAsync(AuditCategory.Authentication, "authentication", request, cancellationToken);

    /// <summary>
    /// 导出活动日志（CSV 流）。
    /// </summary>
    /// <param name="request">查询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>CSV 文件流。</returns>
    /// <response code="200">导出流已开始。</response>
    /// <response code="400">参数非法，或结果超过导出上限（<c>1000</c>）。</response>
    /// <response code="403">缺少 <c>audit.read</c>（<c>1502</c>）。</response>
    [HttpPost("activity/export")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public Task ExportActivityAsync(
        [FromBody] ListAuditLogsRequest request,
        CancellationToken cancellationToken) =>
        this.ExportAsync(AuditCategory.Activity, "activity", request, cancellationToken);

    /// <summary>
    /// 导出实现（两类共用）。
    /// </summary>
    /// <param name="category">类别。</param>
    /// <param name="slug">文件名里的类别片段。</param>
    /// <param name="request">查询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>先查后写</b>：<see cref="AuditLogService.ExportAsync"/> 会在超限时抛
    /// <c>1000</c>，此时响应头还没发出去，可以正常返回统一 <c>ApiResult</c>——
    /// 一旦开始写 CSV 正文就再也改不成 JSON 了。
    /// </remarks>
    private async Task ExportAsync(
        AuditCategory category,
        string slug,
        ListAuditLogsRequest request,
        CancellationToken cancellationToken)
    {
        var rows = await _audit
            .ExportAsync(category, request, hostId: null, cancellationToken)
            .ConfigureAwait(false);

        var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);

        this.Response.ContentType = "text/csv; charset=utf-8";
        this.Response.Headers.ContentDisposition = $"attachment; filename=\"audit-{slug}-{stamp}.csv\"";

        // leaveOpen：响应体的生命周期由框架管理，这里只负责把内容写完。
        // UTF8Encoding(false)：BOM 由 AuditCsvWriter 自己写一次，编码器不必再加。
        await using var writer = new StreamWriter(
            this.Response.Body,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);

        await AuditCsvWriter
            .WriteAsync(writer, category, rows, cancellationToken)
            .ConfigureAwait(false);
    }
}
