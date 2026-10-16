using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 用户作用域的角色分配端点（**全局作用域**，需 <c>role.read</c> / <c>role.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>回答的是「这个人拥有什么」</b>——包括他的<b>全局分配</b>与各主机上的分配。
/// 主机视角的「谁能访问这台机器」在 <see cref="AdminHostRoleAssignmentsController"/>。</para>
/// <para><b>为什么全局分配只能从这里改</b>：主机分配端点的作用域由路径固定，
/// 它天然表达不了「这个人在<b>所有</b>主机上都怎样」。把作用域做成请求体里一个可空字段
/// 会让同一个端点有两种语义，而「省略」与「填空」的区别在 JSON 里极易出错。</para>
/// <para><b>⚠️ 这里的 <c>{userId}</c> 是 GUID</b>（不是主机名）——它只出现在管理接口的请求体/路径里，
/// 不进 URL 的资源定位语义；主机名才是对外标识。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/users/{userId}/roles")]
[Authorize]
public sealed class AdminUserRoleAssignmentsController : ControllerBase
{
    private readonly RoleAssignmentService _assignments;

    /// <summary>
    /// 初始化用户角色分配控制器。
    /// </summary>
    /// <param name="assignments">角色分配用例。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AdminUserRoleAssignmentsController(RoleAssignmentService assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        _assignments = assignments;
    }

    /// <summary>
    /// 查询某个用户的全部分配（全局 + 各主机）。
    /// </summary>
    /// <param name="userId">用户主键（GUID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分配列表。</returns>
    /// <response code="200">查询成功（无分配时返回空数组——新建用户到被分配角色之间就是这个状态）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.read</c>（<c>1500</c>）。</response>
    /// <response code="404">用户不存在（<c>1201</c>）。</response>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.RolesRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<RoleAssignmentSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = await _assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 给某个用户分配一个角色。
    /// </summary>
    /// <param name="userId">用户主键（GUID）。</param>
    /// <param name="request">分配请求（角色 + 可选的 <c>host</c>；省略 <c>host</c> 即全局分配）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分配记录。</returns>
    /// <response code="200">分配成功（已存在则返回原有记录）。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">用户（<c>1201</c>）、主机（<c>1300</c>）或角色（<c>1510</c>）不存在。</response>
    /// <remarks>
    /// <b>幂等</b>：同一 (用户, 角色, 作用域) 重复分配返回同一条记录，不换主键。
    /// </remarks>
    [HttpPost("assign")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult<RoleAssignmentSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignAsync(
        Guid userId,
        [FromBody] AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _assignments
            .AssignAsync(
                new AssignRoleRequest
                {
                    UserId = userId,
                    RoleId = request.RoleId,
                    Role = request.Role,
                    Host = request.Host,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 撤销某个用户的角色分配。
    /// </summary>
    /// <param name="userId">用户主键（GUID）。</param>
    /// <param name="request">撤销请求（角色 + 可选的 <c>host</c>；省略 <c>host</c> 即撤销全局分配）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">撤销成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、角色（<c>1510</c>）不存在，或该分配本就不存在（<c>1515</c>）。</response>
    /// <remarks>
    /// <b>撤销走软删除</b>：保留「谁在什么时候撤的」这条事实，也让同一组合可以重新分配。
    /// </remarks>
    [HttpPost("revoke")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(
        Guid userId,
        [FromBody] RevokeRoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _assignments
            .RevokeAsync(
                new RevokeRoleAssignmentRequest
                {
                    UserId = userId,
                    RoleId = request.RoleId,
                    Role = request.Role,
                    Host = request.Host,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
