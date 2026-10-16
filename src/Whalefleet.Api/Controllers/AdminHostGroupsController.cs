using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.HostGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机分组管理端点（**全局作用域**，仅管理员）。
/// </summary>
/// <remarks>
/// <para><b>与 <see cref="HostGroupsController"/> 分开</b>：那个只管「读」（总览页对所有角色可见），
/// 这里只管「写」。混用会让非 admin 的总览页因权限不足整块报错。</para>
/// <para><b>失败路径不在这里处理</b>：名字非法 / 重名 / 分组不存在等一律由
/// <see cref="HostGroupService"/> 抛 <see cref="BusinessException"/>，交给全局异常中间件映射。</para>
/// <para><b>全部用 POST</b>：与 <c>docs/api-spec.md</c> 的 RPC 风格约定一致。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/host-groups")]
[Authorize(Policy = PermissionPolicies.HostsManage)]
public sealed class AdminHostGroupsController : ControllerBase
{
    private readonly HostGroupService _groups;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="groups">分组用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> 为 <see langword="null"/>。</exception>
    public AdminHostGroupsController(HostGroupService groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        _groups = groups;
    }

    /// <summary>
    /// 列出全部分组（管理页专用；与公开列表**同形状**）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分组列表。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="403">非管理员（<c>1500</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostGroupSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var groups = await _groups.ListAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(groups));
    }

    /// <summary>
    /// 新建分组。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新分组。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">名字非法（<c>1000</c>）或已被占用（<c>1304</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<HostGroupSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateHostGroupRequest request,
        CancellationToken cancellationToken)
    {
        var group = await _groups.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(group));
    }

    /// <summary>
    /// 更新分组（改名 / 描述 / 排序权重）。
    /// </summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为更新后的分组。</returns>
    /// <response code="200">更新成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）或名称已被占用（<c>1304</c>）。</response>
    /// <response code="404">分组不存在（<c>1305</c>）。</response>
    [HttpPost("{groupId:guid}/update")]
    [ProducesResponseType(typeof(ApiResult<HostGroupSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        Guid groupId,
        [FromBody] UpdateHostGroupRequest request,
        CancellationToken cancellationToken)
    {
        var group = await _groups.UpdateAsync(groupId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(group));
    }

    /// <summary>
    /// 删除分组（组内主机回到未分组，**不**删主机）。
    /// </summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为被移出分组的主机数量。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="404">分组不存在（<c>1305</c>）。</response>
    [HttpPost("{groupId:guid}/delete")]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var moved = await _groups.DeleteAsync(groupId, cancellationToken).ConfigureAwait(false);

        // 回带被移出的主机数：界面要据此提示「已把 N 台主机移出该分组」，
        // 否则管理员删完分组会发现主机「不见了」（其实只是回到了未分组）
        return this.Ok(ApiResults.Ok(moved));
    }
}
