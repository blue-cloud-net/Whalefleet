using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Users;
using Whalefleet.Core.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 用户管理端点（**全局作用域**）。
/// </summary>
/// <remarks>
/// <para><b>为什么路径在 <c>/admin/</c> 下</b>：与主机管理保持一致——用户管理整体属于管理面。
/// 其中「修改密码」允许用户改**自己**的密码，因此该动作单独放宽（见其 <c>[Authorize]</c>），
/// 但路径仍留在同一资源下，避免同一个资源出现两套入口。</para>
/// <para><b>「改自己」与「改他人」的区分在服务层</b>：它取决于请求体里的 <c>userId</c>，
/// 是数据相关的判断，<c>[Authorize]</c> 表达不了。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize]
public sealed class AdminUsersController : ControllerBase
{
    private readonly UserService _users;

    /// <summary>
    /// 初始化用户管理控制器。
    /// </summary>
    /// <param name="users">用户管理用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="users"/> 为 <see langword="null"/>。</exception>
    public AdminUsersController(UserService users)
    {
        ArgumentNullException.ThrowIfNull(users);

        _users = users;
    }

    /// <summary>
    /// 分页列出用户。
    /// </summary>
    /// <param name="request">分页参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分页结果。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="403">非管理员（<c>1500</c>）。</response>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.UsersRead)]
    [ProducesResponseType(typeof(ApiResult<UserListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(
        [FromBody] ListUsersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _users.ListAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 创建用户。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新用户。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）、用户名已存在（<c>1200</c>）或密码强度不足（<c>1202</c>）。</response>
    /// <response code="403">非管理员（<c>1500</c>）。</response>
    [HttpPost("create")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult<UserSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _users.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(user));
    }

    /// <summary>
    /// 删除用户（软删除）并撤销其全部会话。
    /// </summary>
    /// <param name="request">删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">未提供 <c>userId</c>，或目标是受保护的账号（<c>1000</c>）。</response>
    /// <response code="404">用户不存在（<c>1201</c>）。</response>
    /// <response code="403">非管理员（<c>1500</c>）。</response>
    [HttpPost("delete")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteAsync(
        [FromBody] DeleteUserRequest request,
        CancellationToken cancellationToken)
    {
        await _users.DeleteAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 修改密码：省略 <c>userId</c> 表示改自己（须提供 <c>oldPassword</c>）；指定他人需管理员。
    /// </summary>
    /// <param name="request">改密请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">修改成功（该用户的全部会话已撤销，需重新登录）。</response>
    /// <response code="400">字段非法或原密码不正确（<c>1000</c>）、新密码强度不足（<c>1202</c>）。</response>
    /// <response code="403">非管理员却试图重置他人密码（<c>1500</c>）。</response>
    /// <response code="404">目标用户不存在（<c>1201</c>）。</response>
    [HttpPost("update-password")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdatePasswordAsync(
        [FromBody] UpdatePasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _users.UpdatePasswordAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
