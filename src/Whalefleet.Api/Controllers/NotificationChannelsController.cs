using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Alerting;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 通知渠道端点（<b>全局作用域</b>，需要 <c>alert.read</c> / <c>alert.write</c>）。
/// </summary>
/// <remarks>
/// <b>响应绝不回传令牌 / 密码</b>：只有脱敏地址、收件人与「是否配置了令牌」。
/// </remarks>
[ApiController]
[Route("api/v1/admin/notification-channels")]
[Authorize]
public sealed class NotificationChannelsController : ControllerBase
{
    private readonly NotificationChannelService _channels;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    public NotificationChannelsController(NotificationChannelService channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        _channels = channels;
    }

    /// <summary>
    /// 列出全部渠道。
    /// </summary>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.AlertsRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<NotificationChannelDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _channels.ListAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 新建渠道。
    /// </summary>
    [HttpPost("create")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult<NotificationChannelDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] SaveNotificationChannelRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _channels.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 更新渠道。
    /// </summary>
    [HttpPost("update")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult<NotificationChannelDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] SaveNotificationChannelRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _channels.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 删除渠道。
    /// </summary>
    [HttpPost("remove")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RemoveNotificationChannelRequest request,
        CancellationToken cancellationToken)
    {
        await _channels.RemoveAsync(request.Id, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 发送测试消息。
    /// </summary>
    [HttpPost("test")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TestAsync(
        [FromBody] TestNotificationChannelRequest request,
        CancellationToken cancellationToken)
    {
        await _channels.TestAsync(request.Id, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
