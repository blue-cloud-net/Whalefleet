using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 编排 Stack 端点（**主机作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与其它主机作用域端点同一套守卫</b>：主机是否存在（<c>1300</c>）与当前用户能否看见它
/// （<c>1501</c>）由 <c>CurrentHostMiddleware</c> 在进入动作方法之前完成。</para>
/// <para><b>写操作的语义分工</b>：<c>deploy</c> 要求「这个名字还没有被本平台部署过」，
/// <c>update</c> 要求「本平台存着它的 YAML」。两者分开而不是合并成一个 upsert，
/// 是因为两者的失败原因完全不同——前者是<b>重名</b>，后者是<b>「没有 YAML 可更新」</b>，
/// 混在一起时用户只会看到一句含糊的「操作失败」。</para>
/// <para><b>部署类端点返回 202 而非 200</b>：部署可能要拉镜像（几分钟），
/// 而 HTTP 请求扛不住那么长——反代会把连接掐掉，客户端拿到的是「504 网关超时」
/// 而不是「部署失败」。因此立刻返回任务标识，进度靠实时通道推送。</para>
/// <para><b>校验在请求期、执行在后台</b>：YAML 语法错、字段不支持、名字非法这些
/// 在 HTTP 响应里直接报出来，用户改一下就能重来。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/stacks")]
[Authorize]
public sealed class StacksController : ControllerBase
{
    private readonly StackService _stacks;
    private readonly StackWebhookService _webhooks;

    /// <summary>
    /// 初始化编排控制器。
    /// </summary>
    /// <param name="stacks">编排用例。</param>
    /// <param name="webhooks">Webhook 用例。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public StacksController(StackService stacks, StackWebhookService webhooks)
    {
        ArgumentNullException.ThrowIfNull(stacks);
        ArgumentNullException.ThrowIfNull(webhooks);

        _stacks = stacks;
        _webhooks = webhooks;
    }

    /// <summary>
    /// 列出主机上的编排 Stack。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（搜索与分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为当前页数据与总数。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<StackListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListStacksRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _stacks
            .ListAsync(hostId, request ?? new ListStacksRequest(), cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询编排 Stack 详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">详情请求（Stack 名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为 Stack 详情（含服务列表与 YAML 原文）。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Stack（<c>1900</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("inspect")]
    [ProducesResponseType(typeof(ApiResult<StackDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> InspectAsync(
        string hostId,
        [FromBody] InspectStackRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _stacks.InspectAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(detail));
    }

    /// <summary>
    /// 部署编排 Stack（新建）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">部署请求（名称、YAML、任务标识、拉取与清理选项）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称非法（<c>1000</c>）、YAML 非法（<c>1904</c>）或同名已存在（<c>1902</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>stack.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Stack 已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("deploy")]
    [ProducesResponseType(typeof(ApiResult<StackTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DeployAsync(
        string hostId,
        [FromBody] ApplyStackRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _stacks.DeployAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 更新编排 Stack（重新应用 YAML）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">更新请求（名称、YAML、任务标识、拉取与清理选项）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称非法（<c>1000</c>）或 YAML 非法（<c>1904</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>stack.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Stack（<c>1900</c>）不存在。</response>
    /// <response code="409">该 Stack 已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("update")]
    [ProducesResponseType(typeof(ApiResult<StackTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> UpdateAsync(
        string hostId,
        [FromBody] ApplyStackRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _stacks.UpdateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 停止编排 Stack（停掉全部服务，容器保留）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">停止请求（名称、任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>stack.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Stack 已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("stop")]
    [ProducesResponseType(typeof(ApiResult<StackTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StopAsync(
        string hostId,
        [FromBody] StopStackRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _stacks.StopAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 删除编排 Stack（容器与网络一并清掉；卷按需）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（名称、任务标识、是否连卷一起删）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>stack.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Stack 已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult<StackTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveStackRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _stacks.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 查询编排任务快照。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务快照；任务不存在时 <c>data</c> 为 <see langword="null"/>。</returns>
    /// <response code="200">查询成功（任务不存在时 <c>data</c> 为 <see langword="null"/>）。</response>
    /// <response code="400">任务标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("task")]
    [ProducesResponseType(typeof(ApiResult<StackTaskSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TaskAsync(
        string hostId,
        [FromBody] GetStackTaskRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _stacks.GetTaskAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(snapshot));
    }

    /// <summary>
    /// 生成（或轮换）编排 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Stack 名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为令牌信息；<b>明文只此一次</b>。</returns>
    /// <response code="200">生成成功。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>stack.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Stack（<c>1900</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("webhook/rotate")]
    [ProducesResponseType(typeof(ApiResult<StackWebhookInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RotateWebhookAsync(
        string hostId,
        [FromBody] StackWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var info = await _webhooks.RotateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(info));
    }

    /// <summary>
    /// 吊销编排 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Stack 名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为吊销后的令牌信息。</returns>
    /// <response code="200">吊销成功（幂等：本来就没启用也算成功）。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>stack.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Stack（<c>1900</c>）不存在。</response>
    [HttpPost("webhook/revoke")]
    [ProducesResponseType(typeof(ApiResult<StackWebhookInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeWebhookAsync(
        string hostId,
        [FromBody] StackWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var info = await _webhooks.RevokeAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(info));
    }
}
