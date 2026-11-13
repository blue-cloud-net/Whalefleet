using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Teams;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 团队管理端点（**全局作用域**，路径不含 <c>hostId</c>）。
/// </summary>
/// <remarks>
/// <para><b>团队只收窄、不扩权</b>：给团队授角色只是设定成员在已授权作用域内的权限上限；
/// 成员的<b>个人</b>授权由 <see cref="AdminUserRoleAssignmentsController"/> 管理，
/// 或由本控制器的「同步到成员」写入（那是<b>显式</b>动作）。</para>
/// <para><b>权限</b>：<c>team.read</c> / <c>team.write</c>。它们比 <c>user.write</c> 弱——
/// 拿得到团队管理权的人不能凭此给任何人加权限。</para>
/// <para><b>为什么路径不带 <c>hostId</c></b>：团队是<b>跨主机</b>的概念
/// （同一个团队在多台主机上都可能有权），把它绑在某台主机上会让它变得不可用。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/teams")]
[Authorize]
public sealed class AdminTeamsController : ControllerBase
{
    private readonly TeamService _teams;

    /// <summary>
    /// 初始化团队管理控制器。
    /// </summary>
    /// <param name="teams">团队管理用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="teams"/> 为 <see langword="null"/>。</exception>
    public AdminTeamsController(TeamService teams)
    {
        ArgumentNullException.ThrowIfNull(teams);

        _teams = teams;
    }

    /// <summary>
    /// 分页列出团队。
    /// </summary>
    /// <param name="request">分页参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分页结果。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.read</c>（<c>1500</c>）。</response>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.TeamsRead)]
    [ProducesResponseType(typeof(ApiResult<TeamListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(
        [FromBody] ListTeamsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _teams.ListAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 读取团队详情（成员 + 角色授权）。
    /// </summary>
    /// <param name="teamId">团队主键（GUID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为团队详情。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.read</c>（<c>1500</c>）。</response>
    /// <response code="404">团队不存在（<c>1204</c>）。</response>
    [HttpPost("{teamId}/detail")]
    [Authorize(Policy = PermissionPolicies.TeamsRead)]
    [ProducesResponseType(typeof(ApiResult<TeamDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DetailAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var result = await _teams.GetAsync(teamId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 创建团队。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新团队。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）或团队名已占用（<c>1203</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    [HttpPost("create")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult<TeamSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateTeamRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _teams.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 更新团队。
    /// </summary>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">更新成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队不存在（<c>1204</c>）。</response>
    [HttpPost("update")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UpdateTeamRequest request,
        CancellationToken cancellationToken)
    {
        await _teams.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 删除团队（软删除）。
    /// </summary>
    /// <param name="request">删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队不存在（<c>1204</c>）。</response>
    /// <remarks>
    /// <b>不回收已发放的授权</b>：团队只是权限上限的持有者，
    /// 成员的个人授权是管理员单独授予的，不随团队删除而消失。
    /// </remarks>
    [HttpPost("remove")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RemoveTeamRequest request,
        CancellationToken cancellationToken)
    {
        await _teams.RemoveAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 添加团队成员（<b>不会授予任何权限</b>）。
    /// </summary>
    /// <param name="request">成员请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为实际新增的成员数。</returns>
    /// <response code="200">添加成功（重复添加返回 0）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队（<c>1204</c>）或用户不存在（<c>1201</c>）。</response>
    [HttpPost("{teamId}/members/add")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddMembersAsync(
        Guid teamId,
        [FromBody] UpdateTeamMembersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _teams
            .AddMembersAsync(request with { TeamId = teamId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 移除团队成员（其个人授权不受影响）。
    /// </summary>
    /// <param name="request">成员请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为实际移除的成员数。</returns>
    /// <response code="200">移除成功（重复移除返回 0）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队不存在（<c>1204</c>）。</response>
    [HttpPost("{teamId}/members/remove")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMembersAsync(
        Guid teamId,
        [FromBody] UpdateTeamMembersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _teams
            .RemoveMembersAsync(request with { TeamId = teamId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 给团队授予角色（<b>权限上限</b>，幂等）。
    /// </summary>
    /// <param name="request">授权请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为团队角色授权记录。</returns>
    /// <response code="200">授予成功（已存在时返回原有记录）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队（<c>1204</c>）、角色（<c>1510</c>）或主机（<c>1300</c>）不存在。</response>
    [HttpPost("{teamId}/roles/assign")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult<TeamRoleAssignmentSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignRoleAsync(
        Guid teamId,
        [FromBody] TeamRoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _teams
            .AssignRoleAsync(request with { TeamId = teamId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 撤销团队的角色授权（软删除）。
    /// </summary>
    /// <param name="request">授权请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">撤销成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队（<c>1204</c>）、角色（<c>1510</c>）不存在，或该授权不存在（<c>1516</c>）。</response>
    [HttpPost("{teamId}/roles/revoke")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeRoleAsync(
        Guid teamId,
        [FromBody] TeamRoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        await _teams
            .RevokeRoleAsync(request with { TeamId = teamId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 把团队的角色授权<b>同步</b>到成员的个人授权（显式动作，幂等）。
    /// </summary>
    /// <param name="request">同步请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为同步结果（新增 / 跳过 / 成员数）。</returns>
    /// <response code="200">同步完成。</response>
    /// <response code="400">字段非法（<c>1000</c>）或团队还没有任何角色授权。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>team.write</c>（<c>1500</c>）。</response>
    /// <response code="404">团队不存在（<c>1204</c>）。</response>
    /// <remarks>
    /// <b>加入团队不会自动调用它</b>：团队只收窄权限，
    /// 成员的个人授权必须由管理员显式写入。
    /// </remarks>
    [HttpPost("{teamId}/sync")]
    [Authorize(Policy = PermissionPolicies.TeamsWrite)]
    [ProducesResponseType(typeof(ApiResult<TeamSyncResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncAsync(
        Guid teamId,
        [FromBody] SyncTeamRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _teams
            .SyncAsync(request with { TeamId = teamId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
