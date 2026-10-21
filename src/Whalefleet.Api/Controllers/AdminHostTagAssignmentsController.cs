using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.HostTags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机 ↔ 标签的批量打标 / 清标端点（**全局作用域**，仅管理员）。
/// </summary>
/// <remarks>
/// <para><b>为什么路径前缀是 <c>admin/hosts/tags</c> 而控制器却独立</b>：
/// 这两个端点的主语是<b>主机</b>（一次给多台主机打标签），
/// 与「某台主机的分组归属」（<c>admin/hosts/{hostId}/group</c>）同族；
/// 但它们的请求体是批量列表、没有 <c>{hostId}</c> 路径参数，
/// 因此单独一个控制器比塞进 <see cref="AdminHostsController"/> 更清楚。</para>
/// <para><b>路由不冲突</b>：<c>admin/hosts/tags/assign</c> 与 <c>admin/hosts/{hostId}/…</c>
/// 的第一段虽然都是 <c>hosts</c>，但字面量段优先级高于参数段——
/// <c>tags</c> 永远会被本控制器匹配，不会落到「主机名 = tags」那条路径上。</para>
/// <para><b>幂等</b>：重复打同一个标签、重复清同一个标签都返回成功。
/// 前端允许「全选 + 批量打标」这种必然包含重复的操作。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/hosts/tags")]
[Authorize(Policy = PermissionPolicies.HostsManage)]
public sealed class AdminHostTagAssignmentsController : ControllerBase
{
    private readonly HostTagService _tags;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="tags">标签用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> 为 <see langword="null"/>。</exception>
    public AdminHostTagAssignmentsController(HostTagService tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        _tags = tags;
    }

    /// <summary>
    /// 批量打标（幂等）。
    /// </summary>
    /// <param name="request">打标请求（主机名列表 + 标签主键列表）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，无 <c>data</c>。</returns>
    /// <response code="200">打标成功（包含「本来就已打过」的情形）。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或标签不存在（<c>1306</c>）。</response>
    [HttpPost("assign")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignAsync(
        [FromBody] HostTagAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        await _tags.AssignAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 批量清标（幂等）。
    /// </summary>
    /// <param name="request">清标请求（主机名列表 + 标签主键列表）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，无 <c>data</c>。</returns>
    /// <response code="200">清标成功（包含「本来就没有该标签」的情形）。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或标签不存在（<c>1306</c>）。</response>
    [HttpPost("revoke")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(
        [FromBody] HostTagAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        await _tags.RevokeAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
