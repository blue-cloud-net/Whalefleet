using Docker.Manager.Application.Common;
using Docker.Manager.Application.Hosts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docker.Manager.Api.Controllers;

/// <summary>
/// 主机概览端点（**主机作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>路径里没有动作名</b>：这个端点读的就是「主机这一整块概览」，不像
/// <c>containers/list</c> 那样需要一个动作段去区分同类资源上的多个操作；多写一段
/// <c>/overview/get</c> 只是噪音。</para>
/// <para><b>与 <c>/api/v1/admin/hosts</c> 的区别</b>：那边的列表是「有哪些主机」（管理视角，
/// 仅管理员），这里的「单台主机什么版本、跑了多少个容器」是实际使用主机时看的东西（任意已认证用户），
/// 也是前端「主机概览」页面的数据源。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/overview")]
[Authorize]
public sealed class HostOverviewController : ControllerBase
{
    private readonly HostOverviewService _overview;

    /// <summary>
    /// 初始化主机概览控制器。
    /// </summary>
    /// <param name="overview">主机概览查询用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="overview"/> 为 <see langword="null"/>。</exception>
    public HostOverviewController(HostOverviewService overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        _overview = overview;
    }

    /// <summary>
    /// 查询主机概览（Docker 版本 + 运行摘要）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为版本信息与运行摘要。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResult<HostOverviewSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> OverviewAsync(string hostId, CancellationToken cancellationToken)
    {
        var overview = await _overview.GetAsync(hostId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(overview));
    }
}
