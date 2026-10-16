using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 主机作用域的角色分配端点（**全局作用域路由**，需 <c>role.read</c> / <c>role.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>回答的是「谁能访问这台机器」</b>。用户视角的「这个人拥有什么」在
/// <see cref="AdminUserRoleAssignmentsController"/>——两者回答的是不同问题，
/// 合并成一个带可空 <c>hostId</c> 的端点只会让前端要么多查一次、要么拿不到主机名。</para>
/// <para><b><c>{hostId}</c> 是主机名</b>，与 <c>admin/hosts/{hostId}/update</c> 一致；
/// 分配表里的 <c>host_id</c> 是 <c>hosts.id</c>（GUID），转换在用例层完成。
/// 这样前端不必为了调分配接口先查一遍主机的 GUID。</para>
/// <para><b>⚠️ 本控制器不放在 <c>/api/v1/hosts/{hostId}/...</c> 下</b>：那里会经过
/// <c>CurrentHostMiddleware</c> 的 <c>host.read</c> 校验，而「给某台主机授权」
/// 需要的是<b>还没被授权的人</b>也能被管理员看见。用管理员自己的权限去守这道门是可行的，
/// 但会让路由前缀与「主机资源」混为一谈。</para>
/// <para><b>失败路径不在这里处理</b>：用户不存在 <c>1201</c>、主机不存在 <c>1300</c>、
/// 角色不存在 <c>1510</c>、分配不存在 <c>1515</c> 一律由
/// <see cref="RoleAssignmentService"/> 抛 <see cref="BusinessException"/>。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/hosts/{hostId}/roles")]
[Authorize]
public sealed class AdminHostRoleAssignmentsController : ControllerBase
{
    private readonly RoleAssignmentService _assignments;

    /// <summary>
    /// 初始化主机角色分配控制器。
    /// </summary>
    /// <param name="assignments">角色分配用例。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AdminHostRoleAssignmentsController(RoleAssignmentService assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        _assignments = assignments;
    }

    /// <summary>
    /// 查询某台主机上的分配（<b>不含全局分配</b>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（可指定单个用户）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分配列表。</returns>
    /// <response code="200">查询成功（无分配时返回空数组）。</response>
    /// <response code="400">主机名为空（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.read</c>（<c>1500</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或指定的用户（<c>1201</c>）不存在。</response>
    /// <remarks>
    /// <b>全局分配不在结果里</b>：它对所有主机生效，无法归属到某一台。
    /// 界面要表达「这些人因为全局分配也能访问这台机器」需要另查一次分配列表——
    /// 刻意不做成隐式合并，因为那会让管理员无法区分「专门给这台机器开的人」与
    /// 「本来就什么都能看到的人」，而这两者的运维含义完全不同。
    /// </remarks>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.RolesRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<RoleAssignmentSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListHostRoleAssignmentsRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _assignments
            .ListByHostAsync(hostId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 把某个角色分配给某个用户（作用域即本主机）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">分配请求（用户主键 + 角色）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分配记录。</returns>
    /// <response code="200">分配成功（已存在则返回原有记录）。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、用户（<c>1201</c>）或角色（<c>1510</c>）不存在。</response>
    /// <remarks>
    /// <b>幂等</b>：对已有 (用户, 角色, 本主机) 组合再次分配返回<b>同一条</b>记录，
    /// 不会换一个新主键——否则界面刷新后会看到一条 id 不同的「新」记录。
    /// </remarks>
    [HttpPost("assign")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult<RoleAssignmentSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignAsync(
        string hostId,
        [FromBody] AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        // 作用域由路径决定，请求体里的 host 字段（若有）一律忽略：
        // 两处都能表达作用域时，客户端传错的那一处就会静默生效。
        var scoped = new AssignRoleRequest
        {
            UserId = request.UserId,
            RoleId = request.RoleId,
            Role = request.Role,
            Host = hostId,
        };

        var result = await _assignments.AssignAsync(scoped, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 撤销某用户在某台主机上的角色分配（软删除）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">撤销请求（用户主键 + 角色）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">撤销成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、角色（<c>1510</c>）不存在，或该分配本就不存在（<c>1515</c>）。</response>
    [HttpPost("revoke")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(
        string hostId,
        [FromBody] RevokeRoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var scoped = new RevokeRoleAssignmentRequest
        {
            UserId = request.UserId,
            RoleId = request.RoleId,
            Role = request.Role,
            Host = hostId,
        };

        await _assignments.RevokeAsync(scoped, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
