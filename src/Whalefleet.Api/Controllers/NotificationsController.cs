using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Alerting;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 站内通知端点（登录用户可用；可见性由服务端按主机权限过滤）。
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationService _notifications;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    public NotificationsController(NotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        _notifications = notifications;
    }

    /// <summary>
    /// 分页列出当前用户可见的通知。
    /// </summary>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<NotificationListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAsync(
        [FromBody] ListNotificationsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _notifications.ListAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 标记已读。
    /// </summary>
    [HttpPost("read")]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReadAsync(
        [FromBody] MarkNotificationsReadRequest request,
        CancellationToken cancellationToken)
    {
        var affected = await _notifications.MarkReadAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(affected));
    }

    /// <summary>
    /// 删除（对本人隐藏）通知。
    /// </summary>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RemoveNotificationsRequest request,
        CancellationToken cancellationToken)
    {
        var affected = await _notifications.DismissAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(affected));
    }
}
