using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Abstractions;

using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Webhooks;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 容器重建 Webhook 的管理端点（**主机作用域**，读写都用 <c>container.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么用 <c>container.write</c> 而不是新开一个权限</b>：
/// 令牌的效力就是「重建这个容器」——它与界面上点「重建」是同一件事，
/// 因此授权口径必须一致。为它单开一条权限会让「能重建容器但管不了令牌」这种
/// 半吊子状态出现在权限矩阵里，而那种分裂没有任何安全收益。</para>
/// <para><b>主机是否存在（<c>1300</c>）与主机可见性（<c>1501</c>）由
/// <c>CurrentHostMiddleware</c> 统一处理</b>，端点内部不再重复校验。</para>
/// <para><b>令牌明文只返回一次</b>：生成 / 轮换时把明文交给界面，之后只能看到提示。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/containers/webhook")]
[Authorize]
public sealed class ContainerWebhookManagementController : ControllerBase
{
    private readonly ContainerWebhookService _webhooks;
    private readonly ContainerAuthorizationService _authorization;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="webhooks">容器 Webhook 用例。</param>
    /// <param name="authorization">权限判定。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerWebhookManagementController(
        ContainerWebhookService webhooks,
        ContainerAuthorizationService authorization)
    {
        ArgumentNullException.ThrowIfNull(webhooks);
        ArgumentNullException.ThrowIfNull(authorization);

        _webhooks = webhooks;
        _authorization = authorization;
    }

    /// <summary>
    /// 列出该主机上已配置的容器 Webhook。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为 Webhook 列表（<b>不含明文</b>）。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>container.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<ContainerWebhookSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(string hostId, CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "container.write", cancellationToken)
            .ConfigureAwait(false);

        var webhooks = await _webhooks.ListAsync(hostId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(webhooks));
    }

    /// <summary>
    /// 生成（或轮换）某个容器的 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（容器名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 含<b>令牌明文（只此一次）</b>。</returns>
    /// <response code="200">生成成功。</response>
    /// <response code="400">容器名为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>container.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("rotate")]
    [ProducesResponseType(typeof(ApiResult<ContainerWebhookInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateAsync(
        string hostId,
        [FromBody] ContainerWebhookContainerRequest request,
        CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "container.write", cancellationToken)
            .ConfigureAwait(false);

        var info = await _webhooks.RotateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(info));
    }

    /// <summary>
    /// 吊销某个容器的 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（容器名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应。</returns>
    /// <response code="200">吊销成功。</response>
    /// <response code="400">容器名为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>container.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Webhook 记录（<c>1915</c>）不存在。</response>
    [HttpPost("revoke")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(
        string hostId,
        [FromBody] ContainerWebhookContainerRequest request,
        CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "container.write", cancellationToken)
            .ConfigureAwait(false);

        await _webhooks.RevokeAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
