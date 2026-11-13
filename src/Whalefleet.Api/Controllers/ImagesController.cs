using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Files;
using Whalefleet.Application.Images;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 镜像端点（**主机作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与容器端点同一套守卫</b>：主机是否存在（<c>1300</c>）与当前用户能否看见它（<c>1501</c>）
/// 由 <c>CurrentHostMiddleware</c> 在进入动作方法之前完成，因此控制器里看不到任何主机校验代码——
/// 不是漏了，而是在管道里做一次比在每个端点里各写一遍可靠。</para>
/// <para><b>失败路径全在下层</b>：镜像不存在（<c>1401</c>）、主机不可达（<c>1301</c>）、
/// 拉取失败（<c>1402</c>）、镜像被占用（<c>1404</c>）都由 <c>ImageService</c> +
/// <c>DockerErrorTranslator</c> 决定，控制器只负责「取输入 → 调用用例 → 包装成功响应」。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/images")]
[Authorize]
public sealed class ImagesController : ControllerBase
{
    private readonly ImageService _images;
    private readonly ImageBuildPushService _buildPush;
    private readonly ImageTransferService _transfers;
    private readonly TransferService _transferJobs;
    private readonly DownloadTicketStore _tickets;
    private readonly FilesOptions _filesOptions;

    /// <summary>
    /// 初始化镜像控制器。
    /// </summary>
    /// <param name="images">镜像用例。</param>
    /// <param name="buildPush">镜像构建与推送用例（0.4.0）。</param>
    /// <param name="transfers">镜像导入导出用例（0.14.0）。</param>
    /// <param name="transferJobs">传输任务编排（0.14.0）。</param>
    /// <param name="tickets">下载票据登记表（0.14.0）。</param>
    /// <param name="filesOptions">文件传输配置（0.14.0）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public ImagesController(
        ImageService images,
        ImageBuildPushService buildPush,
        ImageTransferService transfers,
        TransferService transferJobs,
        DownloadTicketStore tickets,
        IOptions<FilesOptions> filesOptions)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(buildPush);
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(transferJobs);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(filesOptions);

        _images = images;
        _buildPush = buildPush;
        _transfers = transfers;
        _transferJobs = transferJobs;
        _tickets = tickets;
        _filesOptions = filesOptions.Value;
    }

    /// <summary>
    /// 列出主机上的本地镜像。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（是否含中间层、搜索、分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为当前页数据与总数。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<ImageListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListImagesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _images.ListAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询镜像详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（镜像标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为镜像详情。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">镜像标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或镜像（<c>1401</c>）不存在。</response>
    [HttpPost("inspect")]
    [ProducesResponseType(typeof(ApiResult<ImageDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InspectAsync(
        string hostId,
        [FromBody] InspectImageRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _images.InspectAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(detail));
    }

    /// <summary>
    /// 拉取镜像（一次性等待完成；实时进度推送属 0.4.0）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">拉取请求（仓库名、标签、平台）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为镜像 ID、引用、大小与标签。</returns>
    /// <response code="200">拉取成功（含「本地已是最新」的幂等重拉）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="500">拉取失败（<c>1402</c>，消息带守护进程给的原因）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("pull")]
    [ProducesResponseType(typeof(ApiResult<ImagePullResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PullAsync(
        string hostId,
        [FromBody] PullImageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _images.PullAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 删除本地镜像。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（镜像标识、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为被解绑的标签与被删除的镜像 ID。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">镜像标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或镜像（<c>1401</c>）不存在。</response>
    /// <response code="409">镜像被容器引用或存在子镜像（<c>1404</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult<ImageRemovedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveImageRequest request,
        CancellationToken cancellationToken)
    {
        var removed = await _images.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(removed));
    }

    /// <summary>
    /// 批量删除镜像（**部分成功也算成功**，逐条汇报失败）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">批量删除请求（镜像标识列表、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为汇总结果（含失败明细）。</returns>
    /// <response code="200">执行完毕（可能有部分失败，详见 <c>failures</c>）。</response>
    /// <response code="400">列表为空或超限（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）——整批失败，不是逐条失败。</response>
    /// <remarks>
    /// 与容器侧同名端点共用 <see cref="BatchOperationResult"/> 形状，
    /// 前端的批量操作条因此可以用同一套逻辑处理两者。
    /// </remarks>
    [HttpPost("batch-remove")]
    [ProducesResponseType(typeof(ApiResult<BatchOperationResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BatchRemoveAsync(
        string hostId,
        [FromBody] BatchRemoveImagesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _images.BatchRemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 给镜像打标签（对应 <c>docker tag</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">打标签请求（源镜像、目标仓库、目标标签）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为镜像 ID、新引用与全部标签。</returns>
    /// <response code="200">打标签成功。</response>
    /// <response code="400">字段为空、标签非法或仓库名里含标签（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或源镜像（<c>1401</c>）不存在。</response>
    /// <remarks>
    /// <b>tag 不复制镜像</b>：新标签指向的是<b>同一个镜像 ID</b>，磁盘占用不变。
    /// 界面应把行更新成「同一镜像的另一个标签」，而不是「多了一行镜像」——
    /// 后者会让用户以为副本可以随便删（删掉最后一个标签时镜像确实会消失）。
    /// </remarks>
    [HttpPost("tag")]
    [ProducesResponseType(typeof(ApiResult<ImageTaggedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TagAsync(
        string hostId,
        [FromBody] TagImageRequest request,
        CancellationToken cancellationToken)
    {
        var tagged = await _images.TagAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(tagged));
    }

    /// <summary>
    /// 从上传的构建上下文构建镜像（<b>multipart/form-data</b>，一次性等待完成）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="context">构建上下文 tar 包（<c>tar -cf context.tar .</c> 的产物）。</param>
    /// <param name="request">构建参数（标签、Dockerfile 路径、构建参数等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为镜像 ID、引用、大小与构建日志。</returns>
    /// <response code="200">构建成功。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="413">构建上下文超过上限。</response>
    /// <response code="500">构建失败（<c>1806</c>，消息与 <c>buildLog</c> 带守护进程给的原因）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <para><b>为什么是 multipart 而不是 JSON</b>：构建上下文是二进制 tar，
    /// JSON 装不下。表单里的其余字段仍走 <c>[FromForm]</c> 绑定，
    /// 前端用 <c>FormData</c> 一次提交即可。</para>
    /// <para><b>大小上限</b>：<c>FormOptions.MultipartBodyLengthLimit</c> 默认 128 MB，
    /// 本端点刻意<b>不</b>调高——「上传一个 1 GB 的构建上下文」在浏览器场景里几乎总是误操作，
    /// 而守护进程侧的长构建另有时限问题（见 roadmap 的 SignalR 计划）。</para>
    /// </remarks>
    [HttpPost("build")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResult<ImageBuildResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BuildAsync(
        string hostId,
        IFormFile context,
        [FromForm] BuildImageForm request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        await using var stream = context.OpenReadStream();

        var result = await _buildPush.BuildAsync(
            hostId,
            request.ToRequest(context.Length, stream),
            cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 把本地镜像推送到仓库。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">推送请求（镜像标识、可选 仓库配置主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为推送结果（含目标地址与是否带凭据）。</returns>
    /// <response code="200">推送成功。</response>
    /// <response code="400">
    /// 镜像标识为空或无标签（<c>1000</c>），或 仓库凭据被拒（<c>1804</c>）。
    /// </response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、镜像（<c>1401</c>）或 仓库配置（<c>1800</c>）不存在。</response>
    /// <response code="500">推送失败（<c>1807</c>，消息与 <c>pushLog</c> 带守护进程给的原因）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <b>与 build 一样一次性返回</b>：实时进度推送随 0.5.0 的 SignalR 一起改。
    /// </remarks>
    [HttpPost("push")]
    [ProducesResponseType(typeof(ApiResult<ImagePushResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PushAsync(
        string hostId,
        [FromBody] PushImageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _buildPush.PushAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 清理未被使用的镜像（**不可逆**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">清理请求（是否只清悬空镜像、标签过滤）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为实际删除的镜像清单与回收字节数。</returns>
    /// <response code="200">清理完成（可能一个都没删——没有可清理的镜像时也是成功）。</response>
    /// <response code="400">标签过滤表达式非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="409">另一项清理正在进行中（<c>1404</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <para><b>请求里没有「要删哪些」</b>：选哪些由守护进程按引用计数判断。
    /// 因此界面<b>必须</b>在调用本端点之前先用 <c>POST /hosts/{hostId}/system/df</c>
    /// 展示候选清单并要求确认——那是本端点唯一的安全阀。</para>
    /// <para><b>响应里的清单是事实</b>：它可能与预览不完全一致
    /// （守护进程会跳过某些条目），界面要显示实际结果而不是只显示「清理完成」。</para>
    /// </remarks>
    [HttpPost("prune")]
    [ProducesResponseType(typeof(ApiResult<PruneResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PruneAsync(
        string hostId,
        [FromBody] PruneImagesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _images.PruneAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 导出镜像为 tar（等价 <c>docker save</c>，**后台任务**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">导出请求（镜像标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为任务标识与状态。</returns>
    /// <response code="200">已受理（导出在后台执行）。</response>
    /// <response code="400">镜像标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <remarks>
    /// <para><b>为什么是后台任务而不是同步返回</b>：GB 级镜像的导出要读完整的守护进程数据流
    /// 再写到磁盘，可能好几分钟；HTTP 请求扛不住那么长（反代会掐断连接，
    /// 客户端拿到的是「504」而不是「导出失败」）。</para>
    /// <para><b>为什么任务标识由客户端给</b>：客户端先生成令牌、在 SignalR 上订阅它，
    /// 再把它带进请求——这样「进度事件」与「任务查询」用的是同一个串。</para>
    /// <para><b>导出会去重</b>：同一镜像已有进行中的导出时，本次直接复用那个任务
    /// （响应里 <c>reused=true</c>），不会产出第二份 GB 级文件。</para>
    /// </remarks>
    [HttpPost("export")]
    [ProducesResponseType(typeof(ApiResult<StartExportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportAsync(
        string hostId,
        [FromBody] StartExportRequest request,
        CancellationToken cancellationToken)
    {
        var resourceId = request.ResourceId?.Trim();

        if (String.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("镜像标识不能为空。", nameof(request));
        }

        var jobId = request.ProgressToken?.Trim();

        if (String.IsNullOrWhiteSpace(jobId))
        {
            jobId = Guid.NewGuid().ToString("N");
        }

        var result = _transferJobs.StartExport(
            hostId,
            TransferOperations.ImageExport,
            resourceId,
            jobId,
            resourceId);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询镜像导出任务的状态。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">状态查询请求（任务标识）。</param>
    /// <returns>统一响应，<c>data</c> 为任务状态（完成时含下载地址）。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">任务标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或任务不存在（<c>1410</c>）。</response>
    /// <remarks>
    /// <b>状态查询是权威的，SignalR 只是提示</b>：客户端可能没连上实时通道
    /// （或浏览器把它降级成了长轮询），因此「任务完成没有」必须能从这里问到。
    /// </remarks>
    [HttpPost("export/status")]
    [ProducesResponseType(typeof(ApiResult<TransferJobStatusResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportStatusAsync(
        string hostId,
        [FromBody] TransferJobStatusRequest request,
        CancellationToken cancellationToken)
    {
        var jobId = request.JobId?.Trim();

        if (String.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("任务标识不能为空。", nameof(request));
        }

        var status = _transferJobs.GetStatus(jobId);

        if (status is null)
        {
            throw new BusinessException(
                ErrorCodes.TransferJobNotFound,
                $"导出任务「{jobId}」不存在或已过期");
        }

        return this.Ok(ApiResults.Ok(status));
    }

    /// <summary>
    /// 凭下载票据取走导出产物（**匿名端点**，票据即凭证）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）；仅用于路径一致性，不承担鉴权职责。</param>
    /// <param name="token">下载票据。</param>
    /// <returns>文件流（<c>application/x-tar</c> + <c>Content-Disposition</c>）。</returns>
    /// <response code="200">下载流已开始。</response>
    /// <response code="410">票据无效或已过期（<c>1408</c>）。</response>
    /// <remarks>
    /// <para><b>为什么是匿名端点</b>：大文件下载走的是<b>浏览器原生导航</b>，
    /// 而原生导航带不了 <c>Authorization</c> 头。
    /// 因此凭证是「一次性的短时票据」——它只能取走那一个文件。</para>
    /// <para><b>为什么票据不在下载后立刻作废</b>：浏览器可能因为断线而重试，
    /// 也可能用 Range 请求断点续传。票据的有效期（默认 10 分钟）就是给这些场景留的窗口。</para>
    /// </remarks>
    [HttpGet("download")]
    [AllowAnonymous]
    [DownloadTicket]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status410Gone)]
    public IActionResult DownloadAsync(
        string hostId,
        [FromQuery] string? token)
    {
        var now = DateTime.UtcNow;
        var ticket = _tickets.Resolve(token, now);

        if (ticket is null)
        {
            throw new BusinessException(
                ErrorCodes.DownloadTicketInvalid,
                "下载票据无效或已过期，请重新发起导出。");
        }

        if (!System.IO.File.Exists(ticket.FilePath))
        {
            // 票据在、文件不在：只能是产物被外部删了（或进程重启时清掉了）。
            throw new BusinessException(
                ErrorCodes.DownloadTicketInvalid,
                "导出产物已不存在，请重新发起导出。");
        }

        return this.PhysicalFile(
            ticket.FilePath,
            "application/x-tar",
            ticket.FileName,
            enableRangeProcessing: true);
    }

    /// <summary>
    /// 导入镜像 tar（等价 <c>docker load</c>，**原始请求体**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="repository">镜像仓库名（可空）。</param>
    /// <param name="tag">镜像标签（可空）。</param>
    /// <param name="body">tar 流（请求体）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为导入结果。</returns>
    /// <response code="200">导入成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="413">上传体积超过上限（<c>1407</c>）。</response>
    /// <response code="500">导入失败（<c>1402</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <para><b>为什么是原始请求体而不是 multipart</b>：tar 是二进制，
    /// 走 multipart 会在文件外面再包一层边界——浏览器端还得自己拼，
    /// 而 <c>docker save</c> 的产物本来就是可以直接 <c>curl --data-binary</c> 的裸流。</para>
    /// <para><b>为什么必须放开框架的体积上限</b>：Kestrel 默认 30 MB、
    /// <c>FormOptions</c> 默认 128 MB，而本项目要支持 20 GB。
    /// 放开之后限制由应用层用 <see cref="LimitedReadStream"/> 逐字节执行。</para>
    /// </remarks>
    [HttpPost("import")]
    [Consumes("application/octet-stream")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(typeof(ApiResult<ImageImportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ImportAsync(
        string hostId,
        [FromBody] Stream body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        // 请求头已知长度时**提前拒绝**：否则要先把这 GB 级请求体传给守护进程，
        // 让它读到一半才发现超限——那时网络与磁盘都已经白花了。
        // 不知道长度（chunked）时靠 LimitedReadStream 在流式读取中逐字节兜底。
        if (this.Request.ContentLength is long declaredLength && declaredLength > _filesOptions.MaxUploadBytes)
        {
            throw new BusinessException(
                ErrorCodes.UploadTooLarge,
                $"上传体积超过上限（{_filesOptions.MaxUploadBytes} 字节）");
        }

        await using var limited = new LimitedReadStream(body, _filesOptions.MaxUploadBytes);

        var result = await _transfers
            .ImportAsync(hostId, limited, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
