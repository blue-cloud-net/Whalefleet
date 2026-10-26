using Whalefleet.Application.Common;
using Whalefleet.Application.Networks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 网络端点（**主机作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与数据卷端点同一套守卫</b>：主机是否存在（<c>1300</c>）与当前用户能否看见它（<c>1501</c>）
/// 由 <c>CurrentHostMiddleware</c> 在进入动作方法之前完成，因此控制器里看不到任何主机校验代码。</para>
/// <para><b>失败路径全在下层</b>：网络标识非法（<c>1000</c>）、网络不存在（<c>1401</c>）、
/// 同名或子网重叠 / 仍有容器接入 / 内置网络（均为 <c>1404</c>）都由 <c>NetworkService</c> 决定。</para>
/// <para><b>列表端点收一个只含分页字段的请求体</b>：<c>{ pageIndex?, pageSize? }</c>，
/// 缺字段按「第 0 页、每页 20 条」处理。筛选与排序仍归 <c>0.16.0</c>。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/networks")]
[Authorize]
public sealed class NetworksController : ControllerBase
{
    private readonly NetworkService _networks;

    /// <summary>
    /// 初始化网络控制器。
    /// </summary>
    /// <param name="networks">网络用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="networks"/> 为 <see langword="null"/>。</exception>
    public NetworksController(NetworkService networks)
    {
        ArgumentNullException.ThrowIfNull(networks);

        _networks = networks;
    }

    /// <summary>
    /// 列出主机上的网络。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（分页）；省略按第 0 页、每页 20 条。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为当前页数据与总数。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<NetworkListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListNetworksRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _networks
            .ListAsync(hostId, request ?? new ListNetworksRequest(), cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询网络详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（网络 ID 或名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为网络详情。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">网络标识为空或含非法字符（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或网络（<c>1401</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("inspect")]
    [ProducesResponseType(typeof(ApiResult<NetworkDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> InspectAsync(
        string hostId,
        [FromBody] InspectNetworkRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _networks.InspectAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(detail));
    }

    /// <summary>
    /// 创建网络。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">创建请求（名称、驱动、子网、网关、可否单独接入、标签）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为创建出的网络列表项。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">字段非法（<c>1000</c>，如网段缺前缀长度、IPv6 前缀越界）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">同名网络已存在，或子网与现有网络重叠（<c>1404</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<NetworkSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreateAsync(
        string hostId,
        [FromBody] CreateNetworkRequest request,
        CancellationToken cancellationToken)
    {
        var network = await _networks.CreateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(network));
    }

    /// <summary>
    /// 删除网络（网络仍有容器接入时返回 <c>1404</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（网络 ID 或名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，无 <c>data</c>。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">网络标识为空或含非法字符（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或网络（<c>1401</c>）不存在。</response>
    /// <response code="409">仍有容器接入，或试图删除内置网络（<c>1404</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveNetworkRequest request,
        CancellationToken cancellationToken)
    {
        await _networks.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 把容器接入网络（**幂等**：已在网中视为成功）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">接入请求（容器标识、网络标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无数据）。</returns>
    /// <response code="200">接入成功，或容器本来就在该网络上。</response>
    /// <response code="400">标识为空或含非法字符（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、容器（<c>1401</c>）或网络（<c>1401</c>）不存在。</response>
    /// <response code="409">容器未运行，无法接入（<c>1404</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <b>幂等由服务端实现</b>：先读容器与网络的实际状态，已接入就直接返回成功，
    /// 不依赖守护进程在不同版本上的差异行为。
    /// </remarks>
    [HttpPost("connect")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ConnectAsync(
        string hostId,
        [FromBody] ConnectNetworkRequest request,
        CancellationToken cancellationToken)
    {
        await _networks.ConnectContainerAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 把容器从网络断开（**幂等**：本来就不在网中视为成功）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">断开请求（容器标识、网络标识、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无数据）。</returns>
    /// <response code="200">断开成功，或容器本来就不在该网络上。</response>
    /// <response code="400">标识为空或含非法字符（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、容器（<c>1401</c>）或网络（<c>1401</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <b>断开会让容器立刻失去该网络的连通性</b>，界面必须二次确认。
    /// 强制断开（<c>force</c>）会连累共用网络命名空间的其它容器，默认关闭。
    /// </remarks>
    [HttpPost("disconnect")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DisconnectAsync(
        string hostId,
        [FromBody] DisconnectNetworkRequest request,
        CancellationToken cancellationToken)
    {
        await _networks.DisconnectContainerAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
