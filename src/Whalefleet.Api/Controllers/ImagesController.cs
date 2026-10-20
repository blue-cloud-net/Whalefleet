using Whalefleet.Application.Common;
using Whalefleet.Application.Images;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    /// 初始化镜像控制器。
    /// </summary>
    /// <param name="images">镜像用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> 为 <see langword="null"/>。</exception>
    public ImagesController(ImageService images)
    {
        ArgumentNullException.ThrowIfNull(images);

        _images = images;
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
}
