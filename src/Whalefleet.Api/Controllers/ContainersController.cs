using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 容器查询端点（**主机作用域**，任意已认证用户）。
/// </summary>
/// <remarks>
/// <para><b>路径里的 <c>{hostId}</c> 是本作用域的标志</b>：主机是否存在（<c>1300</c>）与当前用户能否看见它
/// （<c>1501</c>）由 <c>CurrentHostMiddleware</c> 在进入动作方法之前完成，因此控制器里看不到任何主机校验代码——
/// 不是漏了，而是在管道里做一次比在每个端点里各写一遍可靠。</para>
/// <para><b>为什么是 POST 而不是 GET</b>：与 <c>docs/api-spec.md</c> 的 RPC 风格一致。
/// 另外容器标识允许用名称（可能含 <c>/</c> 等字符），放请求体里不必操心路径转义。</para>
/// <para><b>失败路径全在下层</b>：容器不存在（<c>1401</c>）、主机不可达（<c>1301</c>）、
/// Docker 调用失败（<c>1402</c>）都由 <c>ContainerService</c> + <c>DockerErrorTranslator</c> 决定，
/// 控制器只负责「取输入 → 调用用例 → 包装成功响应」。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/containers")]
[Authorize]
public sealed class ContainersController : ControllerBase
{
    private readonly ContainerService _containers;

    /// <summary>
    /// 初始化容器查询控制器。
    /// </summary>
    /// <param name="containers">容器查询用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="containers"/> 为 <see langword="null"/>。</exception>
    public ContainersController(ContainerService containers)
    {
        ArgumentNullException.ThrowIfNull(containers);

        _containers = containers;
    }

    /// <summary>
    /// 列出主机上的容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（是否含已停止、分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为当前页数据与总数。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<ContainerListResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListContainersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _containers.ListAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 查询容器详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（容器标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为容器详情。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">容器标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    [HttpPost("inspect")]
    [ProducesResponseType(typeof(ApiResult<ContainerDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InspectAsync(
        string hostId,
        [FromBody] InspectContainerRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _containers.InspectAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(detail));
    }

    /// <summary>
    /// 读取容器日志（一次性返回，不含实时跟随）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">日志请求（容器标识、行数、起始时间等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为日志文本。</returns>
    /// <response code="200">读取成功。</response>
    /// <response code="400">容器标识为空或 <c>tail</c> 越界（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <response code="408">读取超时（<c>1403</c>）。</response>
    [HttpPost("logs")]
    [ProducesResponseType(typeof(ApiResult<ContainerLogsResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> LogsAsync(
        string hostId,
        [FromBody] ContainerLogsRequest request,
        CancellationToken cancellationToken)
    {
        var logs = await _containers.GetLogsAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(logs));
    }

    /// <summary>
    /// 创建容器（仅创建，不自动启动）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">创建请求（精选字段；详见 <c>docs/api-spec.md</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新容器 ID 与守护进程提示。</returns>
    /// <response code="200">创建成功（**不**代表容器已启动）。</response>
    /// <response code="400">字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或镜像（<c>1401</c>）不存在。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<ContainerCreatedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateAsync(
        string hostId,
        [FromBody] CreateContainerRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _containers.CreateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(created));
    }

    /// <summary>
    /// 启动容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">启动请求（容器标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">启动成功（已在运行的容器同样返回 200）。</response>
    /// <response code="400">容器标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    [HttpPost("start")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StartAsync(
        string hostId,
        [FromBody] StartContainerRequest request,
        CancellationToken cancellationToken)
    {
        await _containers.StartAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 停止容器（先 SIGTERM，等待超时后 SIGKILL）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">停止请求（容器标识、等待秒数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">停止成功（已停止的容器同样返回 200）。</response>
    /// <response code="400">容器标识为空或 <c>waitSeconds</c> 越界（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <response code="408">停止超时（<c>1403</c>）。</response>
    [HttpPost("stop")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> StopAsync(
        string hostId,
        [FromBody] StopContainerRequest request,
        CancellationToken cancellationToken)
    {
        await _containers.StopAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 重启容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">重启请求（容器标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">重启成功。</response>
    /// <response code="400">容器标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    [HttpPost("restart")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RestartAsync(
        string hostId,
        [FromBody] RestartContainerRequest request,
        CancellationToken cancellationToken)
    {
        await _containers.RestartAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 删除容器（**不**连带删除数据卷）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（容器标识、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">容器标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <response code="409">容器正在运行且未指定 <c>force</c>（<c>1404</c>）。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] RemoveContainerRequest request,
        CancellationToken cancellationToken)
    {
        await _containers.RemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 批量删除容器（**部分成功也算成功**，逐条汇报失败）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">批量删除请求（容器标识列表、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为汇总结果（含失败明细）。</returns>
    /// <response code="200">执行完毕（可能有部分失败，详见 <c>failures</c>）。</response>
    /// <response code="400">列表为空或超限（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）——整批失败，不是逐条失败。</response>
    /// <remarks>
    /// <b>为什么需要这条端点</b>：前端原先只能对选中的每一行各发一次请求。
    /// 选 20 个容器就是 20 个 HTTP 往返，每个都要重新握手与鉴权，
    /// 而服务端其实能一次把客户端准备好、顺序处理完。
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
        [FromBody] BatchRemoveContainersRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _containers.BatchRemoveAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
