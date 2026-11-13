using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Users;
using Whalefleet.Core.Users;

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

    /// <summary>
    /// 列出某用户的<b>活跃</b>会话（0.13.0）。
    /// </summary>
    /// <param name="userId">目标用户主键（GUID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为会话列表。</returns>
    /// <response code="200">查询成功（无活跃会话时返回空数组）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>user.read</c>（<c>1500</c>）。</response>
    /// <response code="404">用户不存在（<c>1201</c>）。</response>
    [HttpPost("{userId}/sessions/list")]
    [Authorize(Policy = PermissionPolicies.UsersRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<UserSessionSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = await _users
            .ListSessionsAsync(new ListUserSessionsRequest { UserId = userId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 吊销某个会话（0.13.0）。
    /// </summary>
    /// <param name="userId">目标用户主键（GUID）。</param>
    /// <param name="request">吊销请求（会话 id）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">吊销成功（会话已不存在时同样成功——幂等）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>user.write</c>（<c>1500</c>）。</response>
    /// <response code="404">用户不存在（<c>1201</c>）或该会话不属于此用户（<c>1100</c>）。</response>
    /// <remarks>
    /// 吊销后该会话签发的 access token 在<b>下一次请求</b>即失效，
    /// 不必等 15 分钟过期。
    /// </remarks>
    [HttpPost("{userId}/sessions/revoke")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSessionAsync(
        Guid userId,
        [FromBody] RevokeUserSessionRequest request,
        CancellationToken cancellationToken)
    {
        await _users
            .RevokeSessionAsync(request with { UserId = userId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 吊销某用户的<b>全部</b>会话（0.13.0）。
    /// </summary>
    /// <param name="userId">目标用户主键（GUID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为被吊销的会话数。</returns>
    /// <response code="200">吊销完成。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>user.write</c>（<c>1500</c>）。</response>
    /// <response code="404">用户不存在（<c>1201</c>）。</response>
    [HttpPost("{userId}/sessions/revoke-all")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var revoked = await _users
            .RevokeAllSessionsAsync(new ListUserSessionsRequest { UserId = userId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(revoked));
    }

    /// <summary>
    /// 启用 / 停用账号（0.13.0）。
    /// </summary>
    /// <param name="userId">目标用户主键（GUID）。</param>
    /// <param name="request">请求（<c>enabled</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">操作成功（状态未变时同样成功——幂等）。</response>
    /// <response code="400">字段非法（<c>1000</c>）、目标是当前登录账号或最后一个管理员。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>user.write</c>（<c>1500</c>）。</response>
    /// <response code="404">用户不存在（<c>1201</c>）。</response>
    /// <remarks>
    /// <b>停用会立即让该用户的全部会话失效</b>：撤销全部会话 + 每请求校验 <c>is_disabled</c>。
    /// 启用<b>不恢复</b>已撤销的会话——用户需要重新登录。
    /// </remarks>
    [HttpPost("{userId}/set-enabled")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetEnabledAsync(
        Guid userId,
        [FromBody] SetUserEnabledRequest request,
        CancellationToken cancellationToken)
    {
        await _users
            .SetEnabledAsync(request with { UserId = userId }, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
