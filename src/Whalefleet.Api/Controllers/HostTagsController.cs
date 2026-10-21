using Whalefleet.Application.Common;
using Whalefleet.Application.HostTags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机标签查询端点（**全局作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与 <c>/api/v1/admin/host-tags</c> 的分工</b>：那边是管理（增删改、批量打标，仅管理员）；
/// 这里是「有哪些标签可以拿来过滤」，总览页的过滤栏对所有角色可见，因此对所有已登录用户开放。</para>
/// <para><b>标签不参与授权</b>：能看见标签列表不等于能操作挂着该标签的主机——
/// 主机可见性仍由带作用域的角色分配决定。</para>
/// </remarks>
[ApiController]
[Route("api/v1/host-tags")]
[Authorize]
public sealed class HostTagsController : ControllerBase
{
    private readonly HostTagService _tags;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="tags">标签用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> 为 <see langword="null"/>。</exception>
    public HostTagsController(HostTagService tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        _tags = tags;
    }

    /// <summary>
    /// 列出全部标签（供过滤与打标用；无分页）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为标签列表。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostTagSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var tags = await _tags.ListAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(tags));
    }
}
