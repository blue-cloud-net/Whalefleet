using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Common;
using Whalefleet.Application.Operations;
using Whalefleet.Application.Webhooks;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 容器重建 Webhook 端点（**匿名**，凭据即令牌）。
/// </summary>
/// <remarks>
/// <para><b>路径里不能有 <c>hostId</c></b>：这是匿名端点，主机与容器都由令牌反查得到。
/// 若路径里带上主机标识，<c>CurrentHostMiddleware</c> 会拿它去查库并做可见性校验——
/// 而此时用户还没通过令牌验证，结果是「令牌还没验，主机先报错」，
/// 表现为「怎么调都是 404 / 1501」。</para>
/// <para><b>与 Compose 的 Webhook 分开设路径</b>：<c>/webhooks/composes/{token}</c> 与
/// <c>/webhooks/containers/{token}</c>。合成 <c>/webhooks/{token}</c> 会逼着程序
/// 「靠令牌反查资源类型」，而两类资源的生命周期与授权口径都不同。</para>
/// </remarks>
[ApiController]
[Route("api/v1/webhooks/containers")]
[AllowAnonymous]
public sealed class ContainerWebhooksController : ControllerBase
{
    private readonly ContainerWebhookService _webhooks;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="webhooks">容器 Webhook 用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="webhooks"/> 为 <see langword="null"/>。</exception>
    public ContainerWebhooksController(ContainerWebhookService webhooks)
    {
        ArgumentNullException.ThrowIfNull(webhooks);

        _webhooks = webhooks;
    }

    /// <summary>
    /// 由 Webhook 触发容器重建。
    /// </summary>
    /// <param name="token">Webhook 令牌明文。</param>
    /// <param name="request">触发请求（是否启动、是否同步等待）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="404">令牌无效 / 已吊销（<c>1911</c>）。</response>
    /// <response code="429">触发过于频繁（<c>1912</c>）。</response>
    [HttpPost("{token}")]
    [ProducesResponseType(typeof(ApiResult<ResourceTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TriggerAsync(
        string token,
        [FromBody] ContainerWebhookReloadRequest? request,
        CancellationToken cancellationToken)
    {
        var accepted = await _webhooks
            .TriggerAsync(token, request ?? new ContainerWebhookReloadRequest(), cancellationToken)
            .ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 由 Webhook 查询任务快照。
    /// </summary>
    /// <param name="token">Webhook 令牌明文。</param>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务快照；任务不存在时 <c>data</c> 为 <see langword="null"/>。</returns>
    /// <response code="200">查询成功（任务不存在时 <c>data</c> 为 <see langword="null"/>）。</response>
    /// <response code="400">任务标识为空（<c>1000</c>）。</response>
    /// <response code="404">令牌无效 / 已吊销（<c>1911</c>）。</response>
    [HttpGet("{token}/tasks/{taskId}")]
    [ProducesResponseType(typeof(ApiResult<ResourceTaskSnapshot<ContainerRecreateResult>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TaskAsync(
        string token,
        string taskId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _webhooks.GetTaskAsync(token, taskId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(snapshot));
    }
}
