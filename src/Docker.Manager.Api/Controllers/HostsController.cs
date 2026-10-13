using Docker.Manager.Application.Common;
using Docker.Manager.Application.Hosts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docker.Manager.Api.Controllers;

/// <summary>
/// 主机查询端点（**全局作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与 <c>/api/v1/admin/hosts</c> 的分工</b>：那边是管理（增删改、凭据、测试连通性，仅管理员）；
/// 这里是「我能用哪些主机、它们现在什么状态」，总览页与侧栏主机选择器都靠它，
/// 因此对所有角色开放——按「当前用户可见」过滤（0.3.0 前只有管理员可见，非管理员得到空列表）。</para>
/// <para><b>路径里没有 <c>{hostId}</c> 的 <c>list</c></b>：这是全局作用域端点，
/// 因此 <c>CurrentHostMiddleware</c> 不会介入；只有 <c>{hostId}/overview</c> 才受它保护。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts")]
[Authorize]
public sealed class HostsController : ControllerBase
{
    private readonly HostQueryService _hosts;

    /// <summary>
    /// 初始化主机查询控制器。
    /// </summary>
    /// <param name="hosts">主机查询用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="hosts"/> 为 <see langword="null"/>。</exception>
    public HostsController(HostQueryService hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        _hosts = hosts;
    }

    /// <summary>
    /// 列出当前用户可见的主机（含连通状态）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为主机概览数组（不含资源计数与版本）。</returns>
    /// <response code="200">查询成功（无可主机时返回空数组，而不是 403）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostOverviewSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var hosts = await _hosts.ListVisibleAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(hosts));
    }

    /// <summary>
    /// 查询单台主机的概览（版本 + 资源计数 + 启动时间）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为主机概览；主机离线时除基本字段外均为空。</returns>
    /// <response code="200">查询成功（主机离线同样是 200，<c>status=offline</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("{hostId}/overview")]
    [ProducesResponseType(typeof(ApiResult<HostOverviewSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OverviewAsync(string hostId, CancellationToken cancellationToken)
    {
        var overview = await _hosts.GetOverviewAsync(hostId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(overview));
    }
}
