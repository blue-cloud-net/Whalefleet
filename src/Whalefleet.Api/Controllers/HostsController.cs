using Whalefleet.Application.Common;
using Whalefleet.Application.Hosts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机查询端点（**全局作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与 <c>/api/v1/admin/hosts</c> 的分工</b>：那边是管理（增删改、凭据、测试连通性，仅管理员）；
/// 这里是「我能用哪些主机、它们现在什么状态」，总览页与侧栏主机选择器都靠它，
/// 因此对所有角色开放——按「当前用户可见」过滤（0.1.0 前只有管理员可见，非管理员得到空列表）。</para>
/// <para><b>路径里没有 <c>{hostId}</c> 的 <c>list</c></b>：这是全局作用域端点，
/// 因此 <c>CurrentHostMiddleware</c> 不会介入；只有 <c>{hostId}/overview</c> 才受它保护。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts")]
[Authorize]
public sealed class HostsController : ControllerBase
{
    private readonly HostQueryService _hosts;
    private readonly HostDiskUsageService _diskUsage;

    /// <summary>
    /// 初始化主机查询控制器。
    /// </summary>
    /// <param name="hosts">主机查询用例。</param>
    /// <param name="diskUsage">磁盘用量用例。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HostsController(HostQueryService hosts, HostDiskUsageService diskUsage)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(diskUsage);

        _hosts = hosts;
        _diskUsage = diskUsage;
    }

    /// <summary>
    /// 列出当前用户可见的主机（含连通状态与标签，可选过滤）。
    /// </summary>
    /// <param name="request">过滤条件；<b>允许空 body</b>（等于不过滤）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为主机概览数组（不含资源计数、版本与守护进程信息）。</returns>
    /// <response code="200">查询成功（无可主机时返回空数组，而不是 403）。</response>
    /// <response code="400">过滤参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <remarks>
    /// <b>body 可省略</b>：既有调用方（含集成测试）都是裸 POST、不带 body，
    /// 若声明成必填会让它们全部变成 400。
    /// </remarks>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostOverviewSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] HostListRequest? request,
        CancellationToken cancellationToken)
    {
        var hosts = await _hosts.ListVisibleAsync(request, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// 查询主机磁盘用量（镜像 / 容器 / 卷 / 构建缓存）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="type">
    /// 只要某一类资源（<c>image</c> / <c>container</c> / <c>volume</c> / <c>build-cache</c>）；
    /// 省略表示四类全要。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为用量明细；未查询的类为 <see langword="null"/>。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400"><c>type</c> 非法（<c>1000</c>）或主机已停用（<c>1302</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <b>用 POST 而不是 GET</b>：本项目的所有业务端点都是 POST（见 api-spec 的 RPC 风格约定）；
    /// <c>type</c> 走查询串是因为它只有一个可选值，塞进 body 会让「无 body」的调用方多写一次空对象。
    /// </remarks>
    [HttpPost("{hostId}/system/df")]
    [ProducesResponseType(typeof(ApiResult<HostDiskUsage>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DiskUsageAsync(
        string hostId,
        [FromQuery] string? type,
        CancellationToken cancellationToken)
    {
        var usage = await _diskUsage.GetAsync(hostId, type, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(usage));
    }
}
