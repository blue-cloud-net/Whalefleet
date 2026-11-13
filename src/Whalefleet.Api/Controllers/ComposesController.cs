using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Common;
using Whalefleet.Application.Composes;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 编排 Compose 项目端点（**主机作用域**，任意已认证用户）。
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
[Route("api/v1/hosts/{hostId}/composes")]
[Authorize]
public sealed class ComposesController : ControllerBase
{
    private readonly ComposeProjectService _composes;
    private readonly ComposeWebhookService _webhooks;

    /// <summary>
    /// 初始化编排控制器。
    /// </summary>
    /// <param name="composes">编排用例。</param>
    /// <param name="webhooks">Webhook 用例。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ComposesController(ComposeProjectService composes, ComposeWebhookService webhooks)
    {
        ArgumentNullException.ThrowIfNull(composes);
        ArgumentNullException.ThrowIfNull(webhooks);

        _composes = composes;
        _webhooks = webhooks;
    }

    /// <summary>
    /// 列出主机上的编排 Compose 项目。
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
    [ProducesResponseType(typeof(ApiResult<ComposeProjectListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListComposeProjectsRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _composes
            .ListAsync(hostId, request ?? new ListComposeProjectsRequest(), cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询编排 Compose 项目详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">详情请求（Compose 项目名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为 Compose 项目详情（含服务列表与 YAML 原文）。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Compose 项目（<c>1900</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("inspect")]
    [ProducesResponseType(typeof(ApiResult<ComposeProjectDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> InspectAsync(
        string hostId,
        [FromBody] InspectComposeProjectRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _composes.InspectAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(detail));
    }

    /// <summary>
    /// 部署编排 Compose 项目（新建）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">部署请求（名称、YAML、任务标识、拉取与清理选项）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称非法（<c>1000</c>）、YAML 非法（<c>1904</c>）或同名已存在（<c>1902</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Compose 项目已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("deploy")]
    [ProducesResponseType(typeof(ApiResult<ComposeTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DeployAsync(
        string hostId,
        [FromBody] ApplyComposeProjectRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _composes.DeployAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 更新编排 Compose 项目（重新应用 YAML）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">更新请求（名称、YAML、任务标识、拉取与清理选项）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称非法（<c>1000</c>）或 YAML 非法（<c>1904</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Compose 项目（<c>1900</c>）不存在。</response>
    /// <response code="409">该 Compose 项目已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("update")]
    [ProducesResponseType(typeof(ApiResult<ComposeTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> UpdateAsync(
        string hostId,
        [FromBody] ApplyComposeProjectRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _composes.UpdateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 停止编排 Compose 项目（停掉全部服务，容器保留）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">停止请求（名称、任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Compose 项目已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("stop")]
    [ProducesResponseType(typeof(ApiResult<ComposeTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StopAsync(
        string hostId,
        [FromBody] StopComposeProjectRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _composes.StopAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Accepted(ApiResults.Ok(accepted));
    }

    /// <summary>
    /// 删除编排 Compose 项目（容器与网络一并清掉；卷按需）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（名称、任务标识、是否连卷一起删）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与初始状态。</returns>
    /// <response code="202">已受理，后台执行中。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Compose 项目已有进行中的操作（<c>1905</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult<ComposeTaskAccepted>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveComposeProjectRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _composes.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

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
    [ProducesResponseType(typeof(ApiResult<ComposeTaskSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TaskAsync(
        string hostId,
        [FromBody] GetComposeTaskRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _composes.GetTaskAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(snapshot));
    }

    /// <summary>
    /// 生成（或轮换）编排 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Compose 项目名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为令牌信息；<b>明文只此一次</b>。</returns>
    /// <response code="200">生成成功。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Compose 项目（<c>1900</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("webhook/rotate")]
    [ProducesResponseType(typeof(ApiResult<ComposeWebhookInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RotateWebhookAsync(
        string hostId,
        [FromBody] ComposeWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var info = await _webhooks.RotateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(info));
    }

    /// <summary>
    /// 吊销编排 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Compose 项目名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为吊销后的令牌信息。</returns>
    /// <response code="200">吊销成功（幂等：本来就没启用也算成功）。</response>
    /// <response code="400">名称为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或 Compose 项目（<c>1900</c>）不存在。</response>
    [HttpPost("webhook/revoke")]
    [ProducesResponseType(typeof(ApiResult<ComposeWebhookInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeWebhookAsync(
        string hostId,
        [FromBody] ComposeWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var info = await _webhooks.RevokeAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(info));
    }

    /// <summary>
    /// 导出 Compose 项目的原文（YAML）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">导出请求（项目名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为项目名称与 YAML 原文。</returns>
    /// <response code="200">导出成功。</response>
    /// <response code="400">名称为空或非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">该 Compose 项目不是本平台部署的，没有可导出的原文（<c>1903</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <para><b>为什么不是「下载」而是「返回正文」</b>：YAML 通常只有几 KB，返回 JSON 里的字符串
    /// 最简单——界面可以预览、复制，也可以自己另存为文件。而「下载」要走票据 + 临时文件那一套，
    /// 那是给几百 MB 的镜像 tar 用的机制，用在这里只是徒增复杂度。</para>
    /// <para><b>要求 <c>compose.write</c></b>：YAML 里常有明文密码，这份原文等价于部署能力。</para>
    /// </remarks>
    [HttpPost("export")]
    [ProducesResponseType(typeof(ApiResult<ComposeExportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ExportAsync(
        string hostId,
        [FromBody] ExportComposeProjectRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _composes
            .ExportAsync(hostId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
