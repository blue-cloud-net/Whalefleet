using Docker.Manager.Application.Common;
using Docker.Manager.Application.HostGroups;
using Docker.Manager.Application.Hosts;
using Docker.Manager.Core.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docker.Manager.Api.Controllers;

/// <summary>
/// 主机管理端点（**全局作用域**，仅管理员）。
/// </summary>
/// <remarks>
/// <para><b>为什么是全局作用域</b>：这些端点管理的是「有哪些主机」这件事本身，
/// 不针对某一台主机——路径里因此<b>没有</b> <c>{hostId}</c> 段（只有更新 / 删除的目标在主键段里）。
/// 带主机的资源操作在 <c>/api/v1/hosts/{hostId}/...</c>。</para>
/// <para><b>失败路径不在这里处理</b>：字段非法、主机不存在、主机名重复等一律由 <see cref="HostService"/>
/// 抛 <see cref="BusinessException"/>，交给全局异常中间件统一映射成 HTTP 状态码。
/// 控制器只负责「取输入 → 调用用例 → 包装成功响应」。</para>
/// <para><b>全部用 POST</b>：与 <c>docs/api-spec.md</c> 的 RPC 风格约定一致（非 RESTful），
/// 因此「列表」也是 POST，便于带过滤条件与分页参数而不必发明查询串。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/hosts")]
[Authorize(Roles = Role.AdminName)]
public sealed class AdminHostsController : ControllerBase
{
    private readonly HostService _hosts;
    private readonly HostGroupService _groups;

    /// <summary>
    /// 初始化主机管理控制器。
    /// </summary>
    /// <param name="hosts">主机管理用例。</param>
    /// <param name="groups">分组用例（归属调整用）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AdminHostsController(HostService hosts, HostGroupService groups)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(groups);

        _hosts = hosts;
        _groups = groups;
    }

    /// <summary>
    /// 列出全部主机（含连通状态）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为主机列表。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">非管理员（<c>1500</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var hosts = await _hosts.ListAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(hosts));
    }

    /// <summary>
    /// 新建主机。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新主机。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）或主机名已被占用（<c>1303</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<HostSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateHostRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _hosts.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(host));
    }

    /// <summary>
    /// 更新主机的端点 / 凭据 / 启用状态（主机名不可改）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为更新后的主机。</returns>
    /// <response code="200">更新成功（无变化时同样返回当前状态）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("{hostId}/update")]
    [ProducesResponseType(typeof(ApiResult<HostSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        string hostId,
        [FromBody] UpdateHostRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _hosts.UpdateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(host));
    }

    /// <summary>
    /// 删除主机（软删除）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("{hostId}/delete")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(string hostId, CancellationToken cancellationToken)
    {
        await _hosts.DeleteAsync(hostId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 调整主机的分组归属（<c>groupId</c> 传 <c>null</c> 即移出分组）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">归属请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">调整成功。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或分组不存在（<c>1305</c>）。</response>
    /// <remarks>
    /// <b>为什么单设端点而不是塞进 <c>{hostId}/update</c></b>：那边的语义是「字段未提供表示不改」，
    /// 于是「移到未分组」就没有表达方式——JSON 分不清「没给 <c>groupId</c>」与「<c>groupId: null</c>」。
    /// 凭据那边为同一个原因额外加了 <c>clearCredentials</c> 开关；这里直接给一个显式端点，
    /// 语义只有一个：「把主机设为这个组，或不属于任何组」。
    /// </remarks>
    [HttpPost("{hostId}/group")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignGroupAsync(
        string hostId,
        [FromBody] AssignHostGroupRequest request,
        CancellationToken cancellationToken)
    {
        await _groups.AssignHostAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 测试一份**尚未保存**的主机配置。
    /// </summary>
    /// <param name="request">测试请求（不带 <c>hostId</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为布尔值：能否连通。</returns>
    /// <response code="200">测试完成（<c>data=false</c> 表示连不上，不是错误响应）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    [HttpPost("test")]
    [ProducesResponseType(typeof(ApiResult<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestAsync(
        [FromBody] TestHostRequest request,
        CancellationToken cancellationToken)
    {
        var reachable = await _hosts.TestAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(reachable));
    }
}
