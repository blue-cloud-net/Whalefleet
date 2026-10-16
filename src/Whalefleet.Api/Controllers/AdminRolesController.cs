using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Permissions;
using Whalefleet.Core.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 角色管理端点（**全局作用域**，需 <c>role.read</c> / <c>role.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么角色端点不要求 <c>role.write</c> 全量</b>：读接口（<c>list</c> /
/// <c>permissions</c>）用 <c>role.read</c>，只有会改状态的才用 <c>role.write</c>。
/// 这让「能给某人发只读角色」的人不必顺带拿到改角色定义的权力。</para>
/// <para><b>⚠️ 这里没有 <c>[Authorize(Roles = "admin")]</c></b>：角色声明不再参与授权判定
/// （带作用域的分配装不进单个 claim）。策略由自定义 <c>PermissionAuthorizationHandler</c>
/// 查库实现，因此<b>角色与权限的改动立即生效</b>，不必等 access token 过期。</para>
/// <para><b>失败路径不在这里处理</b>：角色不存在 <c>1510</c>、名字已占用 <c>1511</c>、
/// 仍有分配 <c>1512</c>、内置角色受保护 <c>1513</c> 等一律由 <see cref="RoleService"/>
/// 抛 <see cref="BusinessException"/>，交给全局异常中间件统一映射。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/roles")]
[Authorize]
public sealed class AdminRolesController : ControllerBase
{
    private readonly RoleService _roles;

    /// <summary>
    /// 初始化角色管理控制器。
    /// </summary>
    /// <param name="roles">角色管理用例。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AdminRolesController(RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        _roles = roles;
    }

    /// <summary>
    /// 列出角色（含权限集与当前分配数）。
    /// </summary>
    /// <param name="request">查询请求（可只看自定义角色）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为角色列表。</returns>
    /// <response code="200">查询成功（无角色时返回空数组）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">缺少 <c>role.read</c>（<c>1500</c>）。</response>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.RolesRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<RoleSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(
        [FromBody] ListRolesRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _roles.ListAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 返回权限目录（前端权限矩阵的列定义）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为权限目录。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.read</c>（<c>1500</c>）。</response>
    /// <remarks>
    /// 刻意做成独立端点而不是内嵌在 <c>list</c> 里：权限目录是<b>编译期事实</b>，
    /// 同一页面的每次刷新都拿到同一份。让客户端缓存它比每次拉角色列表时顺带返回更省。
    /// </remarks>
    [HttpPost("permissions")]
    [Authorize(Policy = PermissionPolicies.RolesRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<PermissionDefinition>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public IActionResult ListPermissions()
    {
        return this.Ok(ApiResults.Ok(RoleService.ListPermissionDefinitions()));
    }

    /// <summary>
    /// 创建一个自定义角色。
    /// </summary>
    /// <param name="request">创建请求（标识、可读信息、初始权限集）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新角色。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）、角色名已占用（<c>1511</c>）或未知权限（<c>1514</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    [HttpPost("create")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult<RoleSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _roles.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 修改角色的可读名称与描述。
    /// </summary>
    /// <param name="update">定位（<c>roleId</c> 或 <c>role</c>）与要修改的内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为修改后的角色。</returns>
    /// <response code="200">修改成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">角色不存在（<c>1510</c>）。</response>
    [HttpPost("update")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult<RoleSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UpdateRoleRequest update,
        CancellationToken cancellationToken)
    {
        var result = await _roles.UpdateAsync(update, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 覆盖式设置自定义角色的权限集。
    /// </summary>
    /// <param name="body">定位（<c>roleId</c> 或 <c>role</c>）与新的权限集（允许空数组）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为修改后的角色。</returns>
    /// <response code="200">修改成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）或未知权限（<c>1514</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">角色不存在（<c>1510</c>）。</response>
    /// <remarks>
    /// <b>内置角色返回 <c>1513</c></b>：它的权限集随迁移固定。
    /// 一个能被清空权限集的 <c>operator</c>，已经不是它的名字所声称的那个角色。
    /// </remarks>
    [HttpPost("set-permissions")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult<RoleSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPermissionsAsync(
        [FromBody] SetRolePermissionsRequest body,
        CancellationToken cancellationToken)
    {
        var result = await _roles
            .SetPermissionsAsync(body, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 把某个角色设为默认角色（创建用户时采用）。
    /// </summary>
    /// <param name="request">定位请求（<c>roleId</c> 或 <c>role</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为该角色。</returns>
    /// <response code="200">设置成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">角色不存在（<c>1510</c>）。</response>
    /// <remarks>全库至多一个默认角色，由仓储在同一事务内保证。</remarks>
    [HttpPost("set-default")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult<RoleSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultAsync(
        [FromBody] RoleSelectorRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _roles.SetDefaultAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 删除自定义角色。
    /// </summary>
    /// <param name="request">定位请求（<c>roleId</c> 或 <c>role</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">内置角色受保护（<c>1513</c>）或角色仍有分配（<c>1512</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>role.write</c>（<c>1500</c>）。</response>
    /// <response code="404">角色不存在（<c>1510</c>）。</response>
    /// <remarks>
    /// <b>仍有分配时返回 <c>1512</c> 而不级联撤销</b>：级联会悄悄收回没人要求收回的访问权。
    /// 报这个错是把决定权交还管理员——先看谁在用，再逐个撤销，最后才删角色。
    /// </remarks>
    [HttpPost("remove")]
    [Authorize(Policy = PermissionPolicies.RolesWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RoleSelectorRequest request,
        CancellationToken cancellationToken)
    {
        await _roles.RemoveAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
