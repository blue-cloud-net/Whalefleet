using System.Globalization;
using Whalefleet.Application.Abstractions.Realtime;
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
    private readonly IContainerLogProvider _logProvider;
    private readonly ContainerHealthService _health;

    /// <summary>
    /// 初始化容器查询控制器。
    /// </summary>
    /// <param name="containers">容器查询用例。</param>
    /// <param name="logProvider">容器日志流（下载端点用）。</param>
    /// <param name="health">容器健康与退出原因的批量查询。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainersController(
        ContainerService containers,
        IContainerLogProvider logProvider,
        ContainerHealthService health)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(logProvider);
        ArgumentNullException.ThrowIfNull(health);

        _containers = containers;
        _logProvider = logProvider;
        _health = health;
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
    /// 批量查询容器健康状态与退出原因（列表健康列的数据源）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（容器标识列表，最多 100 项）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 统一响应，<c>data.items</c> 为查到的容器健康信息；<b>查不到的容器直接缺席</b>
    /// （多半是被删掉了，前端显示「—」）。
    /// </returns>
    /// <response code="200">查询成功（部分容器查不到也是 200）。</response>
    /// <response code="400">列表为空、含空白项或超过 100 项（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    /// <response code="502">主机不可达（<c>1301</c>）。</response>
    /// <remarks>
    /// <b>为什么不塞进 <c>list</c></b>：容器列表接口不返回健康与重启计数，取它们只能逐个 inspect；
    /// 而列表每 5 秒刷新一次，若把 N 次 inspect 绑进列表，刷新成本会被放大 N 倍。
    /// 拆成独立端点后，界面可以按「只在这一页、且健康列开着」时才去查。
    /// </remarks>
    [HttpPost("health")]
    [ProducesResponseType(typeof(ApiResult<ContainerHealthResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> HealthAsync(
        string hostId,
        [FromBody] ContainerHealthRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _health.GetAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 读取容器日志（一次性返回，结构化行数组）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">日志请求（容器标识、行数、起止时间等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为日志行。</returns>
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
    /// 下载容器日志（<b>纯文本流</b>，不受行数上限约束）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">下载请求（容器标识、起始时间等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文本流（<c>text/plain</c> + <c>Content-Disposition</c>）。</returns>
    /// <response code="200">下载流已开始。</response>
    /// <response code="400">容器标识为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <remarks>
    /// <para><b>为什么不是 ApiResult</b>：这是文件下载，不是业务数据。
    /// 成功时返回 <c>text/plain</c>，失败时返回 JSON 的 <c>ApiResult</c>——
    /// 客户端按 <c>Content-Type</c> 分辨即可，两条路径的错误码口径保持一致。</para>
    /// <para><b>为什么不受 <c>tail</c> 上限约束</b>：<c>tail</c> 上限约束的是<b>界面首屏</b>的
    /// 响应体大小；下载的语义是「把完整日志拿走」，
    /// 用上限卡住它等于给用户一份被静默截断的文件——那比不给下载更糟。</para>
    /// <para><b>为什么逐行写而不是先读进内存</b>：容器日志可能有几百 MB，
    /// 一次性读进内存会把服务的内存吃光。逐行写并 flush，内存占用与日志大小无关。</para>
    /// </remarks>
    [HttpPost("logs/download")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task DownloadLogsAsync(
        string hostId,
        [FromBody] DownloadContainerLogsRequest request,
        CancellationToken cancellationToken)
    {
        var containerId = request.ContainerId?.Trim();

        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("容器标识不能为空。", nameof(request));
        }

        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.ContentDisposition = BuildContentDisposition(containerId);

        await foreach (var line in _logProvider
            .ExportLogsAsync(hostId, containerId, request.Since, request.IncludeStderr ?? true, cancellationToken)
            .ConfigureAwait(false))
        {
            // 每行补一个换行符：SDK 的逐行回调**不带**行尾换行符
            // （见 ContainerLogParser 的说明），不补的话整个文件会挤成一行。
            await Response.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await Response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 构造下载用的 <c>Content-Disposition</c>。
    /// </summary>
    /// <param name="containerId">容器标识。</param>
    /// <returns>头部值。</returns>
    /// <remarks>
    /// <b>为什么用 <c>FileNameStar</c></b>：容器名可能含中文，而 HTTP 头只允许 ASCII。
    /// <c>FileNameStar</c>（RFC 5987）专门解决这个：给出一个 ASCII 兜底名，
    /// 浏览器优先用 <c>filename*</c> 里的 UTF-8 名。
    /// 只写 <c>filename</c> 的话，中文容器名会变成一串乱码。
    /// </remarks>
    private static string BuildContentDisposition(string containerId)
    {
        var safeId = string.Concat(containerId.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

        if (safeId.Length == 0)
        {
            safeId = "container";
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{safeId}-{stamp}.log";

        return $"attachment; filename=\"{fileName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
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

    /// <summary>
    /// 重命名容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">重命名请求（容器标识、新名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为容器 ID 与新名称。</returns>
    /// <response code="200">重命名成功。</response>
    /// <response code="400">容器标识为空或名称非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <response code="409">新名称已被占用（<c>1404</c>）。</response>
    [HttpPost("rename")]
    [ProducesResponseType(typeof(ApiResult<ContainerRenamedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RenameAsync(
        string hostId,
        [FromBody] RenameContainerRequest request,
        CancellationToken cancellationToken)
    {
        var renamed = await _containers.RenameAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(renamed));
    }

    /// <summary>
    /// 热更新容器的重启策略与资源限制（**只支持守护进程真能热改的字段**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为<b>更新后</b>实际生效的值。</returns>
    /// <response code="200">更新成功。</response>
    /// <response code="400">容器标识为空、全省略或字段非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <remarks>
    /// 镜像、端口、环境变量、挂载、网络**不在本端点能力范围内**——Docker 不支持热改它们，
    /// 改了就得重建容器。返回的是更新后的真实值，前端据此刷新而不是按请求值回显。
    /// </remarks>
    [HttpPost("update")]
    [ProducesResponseType(typeof(ApiResult<ContainerUpdatedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        string hostId,
        [FromBody] UpdateContainerRequest request,
        CancellationToken cancellationToken)
    {
        var updated = await _containers.UpdateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(updated));
    }

    /// <summary>
    /// 复制容器：按原容器配置新建一个兄弟容器（**原容器不受影响**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">复制请求（源容器、新名称、是否启动）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新容器 ID、名称与提示。</returns>
    /// <response code="200">复制成功（原容器保持原状）。</response>
    /// <response code="400">容器标识为空或名称非法（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或源容器（<c>1401</c>）不存在。</response>
    /// <response code="409">名称持续冲突（<c>1404</c>）。</response>
    /// <remarks>
    /// 默认名是「原名 + <c>-copy</c>」，被占用时自动追加 <c>-2</c> / <c>-3</c>，
    /// 因此连点两次不会失败。原容器不会被停止、改名或删除。
    /// </remarks>
    [HttpPost("duplicate")]
    [ProducesResponseType(typeof(ApiResult<ContainerRecreatedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DuplicateAsync(
        string hostId,
        [FromBody] DuplicateContainerRequest request,
        CancellationToken cancellationToken)
    {
        var duplicated = await _containers.DuplicateAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(duplicated));
    }

    /// <summary>
    /// 为已存在容器追加挂载（⚠️ <b>会重建容器</b>，容器 ID 会变）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">追加挂载请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新容器 ID、被让位的旧容器 ID 与运行状态。</returns>
    /// <response code="200">重建成功；<c>replacedContainerId</c> 指向保留着的旧容器。</response>
    /// <response code="400">容器标识为空、挂载为空或目标路径重复（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <response code="409">无法为旧容器找到空闲的让位名（<c>1404</c>）。</response>
    /// <remarks>
    /// <b>界面必须把「会重建容器」写在二次确认里</b>：Docker 没有给运行中容器加挂载的接口，
    /// 只能重建。旧容器不会被删除，而是改名成 <c>whalefleet-replaced-*</c> 保留——
    /// 那是回滚的后手，用户确认新容器没问题后可以自己删。
    /// </remarks>
    [HttpPost("add-volumes")]
    [ProducesResponseType(typeof(ApiResult<ContainerRecreatedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddVolumesAsync(
        string hostId,
        [FromBody] AddContainerVolumesRequest request,
        CancellationToken cancellationToken)
    {
        var recreated = await _containers.AddVolumesAsync(hostId, request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(recreated));
    }
}
