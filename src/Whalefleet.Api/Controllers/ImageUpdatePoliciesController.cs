using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Images;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 镜像自动更新策略端点（<b>主机作用域</b>，读用 <c>image.read</c>、写用
/// <c>image.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>RPC 风格</b>：<c>POST …/update-policies/{list,create,update,remove,check}</c>，
/// 而不是把动作塞进 URL 路径段。</para>
/// <para><b>主机是否存在（<c>1300</c>）与主机可见性（<c>1501</c>）由
/// <c>CurrentHostMiddleware</c> 统一处理</b>，端点内部不重复校验。</para>
/// <para><b>「立即检查」可能触发真实变更</b>：<c>auto</c> 模式下它会拉取镜像并重建
/// 容器，因此该动作要求 <c>image.write</c>。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/images/update-policies")]
[Authorize]
public sealed class ImageUpdatePoliciesController : ControllerBase
{
    private readonly ImageUpdateService _updates;
    private readonly ContainerAuthorizationService _authorization;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="updates">镜像自动更新用例。</param>
    /// <param name="authorization">权限判定。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ImageUpdatePoliciesController(
        ImageUpdateService updates,
        ContainerAuthorizationService authorization)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(authorization);

        _updates = updates;
        _authorization = authorization;
    }

    /// <summary>
    /// 列出某台主机的全部镜像更新策略。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为策略列表。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>image.read</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<ImageUpdatePolicyDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(string hostId, CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "image.read", cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(await _updates.ListAsync(hostId, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// 新增一条策略。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（镜像引用、模式、是否启用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新建的策略。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">镜像引用为空或非法（<c>1000</c>）、该镜像已有策略（<c>1412</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>image.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<ImageUpdatePolicyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateAsync(
        string hostId,
        [FromBody] CreateImageUpdatePolicyRequest request,
        CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "image.write", cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(
            await _updates.CreateAsync(hostId, request, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// 修改策略的执行模式与启用状态。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（策略主键、模式、是否启用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为修改后的策略。</returns>
    /// <response code="200">修改成功。</response>
    /// <response code="400">模式非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>image.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或策略不存在（<c>1411</c>）。</response>
    [HttpPost("update")]
    [ProducesResponseType(typeof(ApiResult<ImageUpdatePolicyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        string hostId,
        [FromBody] UpdateImageUpdatePolicyRequest request,
        CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "image.write", cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(
            await _updates.UpdateAsync(hostId, request, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// 删除策略。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（策略主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无数据）。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>image.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或策略不存在（<c>1411</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveImageUpdatePolicyRequest request,
        CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "image.write", cancellationToken)
            .ConfigureAwait(false);

        await _updates.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 立即检查一条策略（<c>auto</c> 模式下会拉取并重建）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（策略主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为检查后的策略与本次重建的容器数。</returns>
    /// <response code="200">检查完成（结论可能是「有新版本」「已是最新」或「失败」）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>image.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）或策略不存在（<c>1411</c>）。</response>
    /// <remarks>
    /// <b>仓库不可达 / 需授权 / 限流都返回 200</b>：它们是<b>检查结论</b>
    /// （落在 <c>state=failed</c> 与 <c>message</c> 里），而不是请求本身有错。
    /// 把它们译成 4xx/5xx 会让界面上分不清「我点错了」和「仓库今天不通」。
    /// </remarks>
    [HttpPost("check")]
    [ProducesResponseType(typeof(ApiResult<ImageUpdateCheckResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckAsync(
        string hostId,
        [FromBody] CheckImageUpdatePolicyRequest request,
        CancellationToken cancellationToken)
    {
        await _authorization
            .EnsureUnrestrictedAsync(hostId, "image.write", cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(
            await _updates.CheckAsync(hostId, request, cancellationToken).ConfigureAwait(false)));
    }
}
