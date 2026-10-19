using Whalefleet.Application.Common;
using Whalefleet.Application.Volumes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 数据卷端点（**主机作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>与镜像端点同一套守卫</b>：主机是否存在（<c>1300</c>）与当前用户能否看见它（<c>1501</c>）
/// 由 <c>CurrentHostMiddleware</c> 在进入动作方法之前完成，因此控制器里看不到任何主机校验代码——
/// 不是漏了，而是在管道里做一次比在每个端点里各写一遍可靠。</para>
/// <para><b>失败路径全在下层</b>：卷名非法（<c>1000</c>）、卷不存在（<c>1401</c>）、
/// 仍被容器引用（<c>1404</c>）都由 <c>VolumeService</c> + <c>DockerErrorTranslator</c> 决定，
/// 控制器只负责「取输入 → 调用用例 → 包装成功响应」。</para>
/// <para><b>列表端点不收请求体</b>：单台主机上的卷数量少、条目也小，
/// 而分页与筛选是四类资源<b>统一</b>的改动（<c>0.16.0</c>），提前做一半只会让接口返工。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/volumes")]
[Authorize]
public sealed class VolumesController : ControllerBase
{
    private readonly VolumeService _volumes;

    /// <summary>
    /// 初始化数据卷控制器。
    /// </summary>
    /// <param name="volumes">数据卷用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="volumes"/> 为 <see langword="null"/>。</exception>
    public VolumesController(VolumeService volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        _volumes = volumes;
    }

    /// <summary>
    /// 列出主机上的数据卷。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为全部卷与守护进程给出的告警。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<VolumeListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        CancellationToken cancellationToken)
    {
        var result = await _volumes.ListAsync(hostId, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询数据卷详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（卷名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为卷详情。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">卷名为空或含非法字符（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或卷（<c>1401</c>）不存在。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("inspect")]
    [ProducesResponseType(typeof(ApiResult<VolumeDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> InspectAsync(
        string hostId,
        [FromBody] InspectVolumeRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _volumes.InspectAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(detail));
    }

    /// <summary>
    /// 创建数据卷。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">创建请求（卷名、驱动、驱动选项、标签）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为创建出的卷列表项。</returns>
    /// <response code="200">创建成功（含「该名字已存在、原样返回」）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<VolumeSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreateAsync(
        string hostId,
        [FromBody] CreateVolumeRequest request,
        CancellationToken cancellationToken)
    {
        var volume = await _volumes.CreateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(volume));
    }

    /// <summary>
    /// 删除数据卷。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（卷名、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，无 <c>data</c>。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">卷名为空或含非法字符（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或卷（<c>1401</c>）不存在。</response>
    /// <response code="409">卷仍被容器引用且未指定 <c>force</c>（<c>1404</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveVolumeRequest request,
        CancellationToken cancellationToken)
    {
        await _volumes.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 批量删除数据卷（**部分成功也返回 200**，逐条汇报失败）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">批量删除请求（卷名列表、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为汇总结果与逐条失败详情。</returns>
    /// <response code="200">处理完成（含部分成功与全部失败）。</response>
    /// <response code="400">卷名列表为空 / 超过 100 个 / 全是空值（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("batch-remove")]
    [ProducesResponseType(typeof(ApiResult<BatchOperationResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BatchRemoveAsync(
        string hostId,
        [FromBody] BatchRemoveVolumesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _volumes.BatchRemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
