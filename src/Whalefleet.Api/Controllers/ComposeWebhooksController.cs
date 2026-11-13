using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Common;
using Whalefleet.Application.Composes;
using Whalefleet.Application.Operations;
using Whalefleet.Core.Variables;

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
[Route("api/v1/webhooks/composes")]
[AllowAnonymous]
public sealed class ComposeWebhooksController : ControllerBase
{
    private readonly ComposeWebhookService _webhooks;

    /// <summary>
    /// 初始化 Webhook 控制器。
    /// </summary>
    /// <param name="webhooks">Webhook 用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="webhooks"/> 为 <see langword="null"/>。</exception>
    public ComposeWebhooksController(ComposeWebhookService webhooks)
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
    /// <returns>统一响应，<c>data</c> 为任务标识、初始状态与本次的变量覆盖结果。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">试图覆盖密钥（<c>1910</c>）。</response>
    /// <response code="404">令牌无效 / 已吊销（<c>1906</c>）。</response>
    /// <response code="409">该 Compose 项目已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="429">触发过于频繁（<c>1907</c>）。</response>
    [HttpPost("{token}")]
    [ProducesResponseType(typeof(ApiResult<ResourceTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TriggerAsync(
        string token,
        [FromBody] ComposeWebhookReloadRequest? request,
        CancellationToken cancellationToken)
    {
        var overrides = this.ReadOverrides();

        var accepted = await _webhooks
            .TriggerAsync(token, request ?? new ComposeWebhookReloadRequest(), overrides, cancellationToken)
            .ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 从查询串里读出变量覆盖项。
    /// </summary>
    /// <returns>变量名 → 值。</returns>
    /// <remarks>
    /// <para><b>查询串里的键就是变量名</b>：<c>?TAG=3&amp;MODE=dev</c> 表示
    /// 「用 3 覆盖 <c>TAG</c>、用 dev 覆盖 <c>MODE</c>」。
    /// <b>没有 <c>?tag=</c> 这个魔法参数</b>——它等价于「覆盖名为 <c>TAG</c> 的变量」，
    /// 而魔法替换会让「哪个键对应哪个变量」变成隐式约定。</para>
    /// <para><b>白名单在应用服务里判</b>：这里只负责把查询串搬成字典，
    /// 哪些键能用由变量表决定（未声明 / 未勾选可覆盖的键会被忽略并回带名字）。</para>
    /// <para><b>同名键取最后一个</b>；<b>空值与过长的值直接丢弃</b>——
    /// 它们要么是空操作，要么超出变量的长度上限。</para>
    /// </remarks>
    private Dictionary<string, string> ReadOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in this.Request.Query)
        {
            if (String.IsNullOrWhiteSpace(pair.Key) || pair.Value.Count == 0)
            {
                continue;
            }

            var value = pair.Value[^1];

            if (String.IsNullOrEmpty(value) || value.Length > EnvVariable.MaxValueLength)
            {
                continue;
            }

            overrides[pair.Key] = value;
        }

        return overrides;
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
    [ProducesResponseType(typeof(ApiResult<ResourceTaskSnapshot<ComposeOperationResult>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TaskAsync(
        string token,
        [FromBody] ComposeWebhookTaskRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _webhooks.GetTaskAsync(token, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(snapshot));
    }
}
