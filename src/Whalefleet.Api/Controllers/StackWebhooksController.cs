using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 编排 Webhook 端点（**匿名**，凭据即令牌）。
/// </summary>
/// <remarks>
/// <para><b>路径里不能有 <c>hostId</c></b>：这是匿名端点，主机由令牌反查得到。
/// 若路径里带上主机标识，<c>CurrentHostMiddleware</c> 会拿它去查库并做可见性校验——
/// 而此时用户还没通过令牌验证，结果是「令牌还没验，主机先报错」，
/// 表现为「怎么调都是 404 / 1501」。</para>
/// <para><b>匿名不等于无凭据</b>：令牌本身就是凭据，且
/// <b>令牌无效一律 404</b>（不区分「不存在」「已吊销」「未启用」——
/// 区分了就成了令牌探测工具）。</para>
/// <para><b>为什么支持同步等待</b>：CI 通常需要「部署完再退出」，
/// 而无限等待会被反代掐断（nginx 默认读超时 60s）。因此等待有上限，
/// 超时后返回 202 + 任务标识，CI 可改用轮询。</para>
/// </remarks>
[ApiController]
[Route("api/v1/webhooks/stacks")]
[AllowAnonymous]
public sealed class StackWebhooksController : ControllerBase
{
    private readonly StackWebhookService _webhooks;

    /// <summary>
    /// 初始化 Webhook 控制器。
    /// </summary>
    /// <param name="webhooks">Webhook 用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="webhooks"/> 为 <see langword="null"/>。</exception>
    public StackWebhooksController(StackWebhookService webhooks)
    {
        ArgumentNullException.ThrowIfNull(webhooks);

        _webhooks = webhooks;
    }

    /// <summary>
    /// 由 Webhook 触发编排重放（重新应用 YAML）。
    /// </summary>
    /// <param name="token">Webhook 令牌明文。</param>
    /// <param name="request">重放请求（拉取与清理选项、是否同步等待）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="404">令牌无效 / 已吊销（<c>1906</c>）。</response>
    /// <response code="409">该 Stack 已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="429">触发过于频繁（<c>1907</c>）。</response>
    [HttpPost("{token}")]
    [ProducesResponseType(typeof(ApiResult<StackTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TriggerAsync(
        string token,
        [FromBody] StackWebhookReloadRequest? request,
        CancellationToken cancellationToken)
    {
        var accepted = await _webhooks
            .TriggerAsync(token, request ?? new StackWebhookReloadRequest(), cancellationToken)
            .ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 由 Webhook 查询任务快照。
    /// </summary>
    /// <param name="token">Webhook 令牌明文。</param>
    /// <param name="request">查询请求（任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务快照；任务不存在时 <c>data</c> 为 <see langword="null"/>。</returns>
    /// <response code="200">查询成功（任务不存在时 <c>data</c> 为 <see langword="null"/>）。</response>
    /// <response code="400">任务标识为空（<c>1000</c>）。</response>
    /// <response code="404">令牌无效 / 已吊销（<c>1906</c>）。</response>
    [HttpPost("{token}/task")]
    [ProducesResponseType(typeof(ApiResult<StackTaskSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TaskAsync(
        string token,
        [FromBody] StackWebhookTaskRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _webhooks.GetTaskAsync(token, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(snapshot));
    }
}
