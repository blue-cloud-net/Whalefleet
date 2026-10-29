using Whalefleet.Api.Security;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机「事件」端点（<b>主机作用域</b>）：某台主机上发生过的活动。
/// </summary>
/// <remarks>
/// <para><b>与 <c>/admin/logs/activity/list</c> 的分工</b>：那条是全局审计（需要 <c>audit.read</c>），
/// 这条是「我在看这台主机，想看看它上面发生过什么」——门槛是<b>看得见这台主机</b>（<c>host.read</c>，
/// 由 <c>CurrentHostMiddleware</c> 统一校验）。让看机器的人也能看这台机器上的操作记录，
/// 是运维日常（「刚才谁动了它」）最常问的一句话。</para>
/// <para><b>为什么主机在路径里</b>：项目铁律——<c>hostId</c> 只出现在路径中，请求体不得含它。
/// 请求体里带一个后端不认识的字段不会报错，只会被静默忽略，症状是「筛了这台主机却看到全部」。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/events")]
[Authorize]
public sealed class HostEventsController : ControllerBase
{
    private readonly AuditLogService _audit;
    private readonly IDockerHostAccessor _hosts;

    /// <summary>
    /// 初始化主机事件控制器。
    /// </summary>
    /// <param name="audit">审计查询用例。</param>
    /// <param name="hosts">主机访问器（把路径里的主机名解析成主键，供过滤用）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HostEventsController(AuditLogService audit, IDockerHostAccessor hosts)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(hosts);

        _audit = audit;
        _hosts = hosts;
    }

    /// <summary>
    /// 分页查询该主机上的活动日志。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分页结果。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">时间区间颠倒或结果取值不认识（<c>1000</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <remarks>
    /// <b>为什么要多查一次主机</b>：审计表里的 <c>host_id</c> 存的是 <c>hosts.id</c>（Guid），
    /// 而路径里是主机名。过滤必须用同一个口径，否则「按主机筛」会静默筛出 0 条。
    /// </remarks>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<AuditLogListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListAuditLogsRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _hosts.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        var result = await _audit
            .ListAsync(AuditCategory.Activity, request, host.Id, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
