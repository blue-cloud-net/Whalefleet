using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.HostTags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机标签管理端点（**全局作用域**，仅管理员）。
/// </summary>
/// <remarks>
/// <para><b>与 <see cref="HostTagsController"/> 分开</b>：那个只管「读」（总览页对所有角色可见），
/// 这里只管「写」。混用会让非 admin 的总览页因权限不足整块报错。</para>
/// <para><b>失败路径不在这里处理</b>：名字非法 / 重名 / 标签不存在等一律由
/// <see cref="HostTagService"/> 抛 <see cref="BusinessException"/>，交给全局异常中间件映射。</para>
/// <para><b>全部用 POST</b>：与 <c>docs/api-spec.md</c> 的 RPC 风格约定一致。</para>
/// <para><b>打标 / 清标不在这里</b>：那两个端点的路径是 <c>admin/hosts/tags/*</c>（主语是主机），
/// 由 <see cref="AdminHostTagAssignmentsController"/> 承担，与「主机级分配」其他端点保持同一前缀。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/host-tags")]
[Authorize(Policy = PermissionPolicies.HostsManage)]
public sealed class AdminHostTagsController : ControllerBase
{
    private readonly HostTagService _tags;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="tags">标签用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> 为 <see langword="null"/>。</exception>
    public AdminHostTagsController(HostTagService tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        _tags = tags;
    }

    /// <summary>
    /// 列出全部标签（管理页专用；与公开列表**同形状**）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为标签列表。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="403">非管理员（<c>1500</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostTagSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var tags = await _tags.ListAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(tags));
    }

    /// <summary>
    /// 新建标签。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新标签。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">名字或颜色非法（<c>1000</c>）或已被占用（<c>1307</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<HostTagSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateHostTagRequest request,
        CancellationToken cancellationToken)
    {
        var tag = await _tags.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(tag));
    }

    /// <summary>
    /// 更新标签（改名 / 描述 / 颜色）。
    /// </summary>
    /// <param name="tagId">标签主键。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为更新后的标签。</returns>
    /// <response code="200">更新成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）或名称已被占用（<c>1307</c>）。</response>
    /// <response code="404">标签不存在（<c>1306</c>）。</response>
    [HttpPost("{tagId:guid}/update")]
    [ProducesResponseType(typeof(ApiResult<HostTagSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        Guid tagId,
        [FromBody] UpdateHostTagRequest request,
        CancellationToken cancellationToken)
    {
        var tag = await _tags.UpdateAsync(tagId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(tag));
    }

    /// <summary>
    /// 删除标签（关联一并清除，**不**删主机）。
    /// </summary>
    /// <param name="tagId">标签主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为被解除关联的主机数量。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="404">标签不存在（<c>1306</c>）。</response>
    [HttpPost("{tagId:guid}/delete")]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid tagId, CancellationToken cancellationToken)
    {
        var affected = await _tags.DeleteAsync(tagId, cancellationToken).ConfigureAwait(false);

        // 回带被解除关联的主机数：界面要据此提示「已把 N 台主机的该标签清除」，
        // 否则管理员删完标签会以为主机也跟着没了
        return this.Ok(ApiResults.Ok(affected));
    }
}
