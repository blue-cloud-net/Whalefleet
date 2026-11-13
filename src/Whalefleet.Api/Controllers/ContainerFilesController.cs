using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Common;
using Whalefleet.Application.Files;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 容器文件系统浏览端点（0.14.0，**主机作用域**）。
/// </summary>
/// <remarks>
/// <para><b>读写分开</b>：<c>list</c> / <c>download</c> 只需要容器读权限；
/// <c>write</c> 需要容器写权限，并受 <c>Files:AllowWrite</c> 开关约束。</para>
/// <para><b>为什么单独一个控制器</b>：文件浏览的路由段是
/// <c>/containers/files/*</c>，与容器的生命周期端点（<c>start</c> / <c>stop</c> …）
/// 不属于同一类动作——前者操作的是「容器里面的东西」，后者操作的是「容器本身」。
/// 放在同一个控制器里会让那个类再长 300 行，而它的职责描述已经装不下这些了。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/containers/files")]
[Authorize]
public sealed class ContainerFilesController : ControllerBase
{
    private readonly ContainerFileService _files;
    private readonly FilesOptions _options;

    /// <summary>
    /// 初始化容器文件浏览控制器。
    /// </summary>
    /// <param name="files">文件浏览用例。</param>
    /// <param name="options">文件传输配置。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerFilesController(ContainerFileService files, IOptions<FilesOptions> options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);

        _files = files;
        _options = options.Value;
    }

    /// <summary>
    /// 列举容器内的一个目录。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">列举请求（容器标识、路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为目录清单。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">容器标识为空或路径非法（<c>1000</c> / <c>1405</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或无容器读权限（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、容器（<c>1401</c>）或路径（<c>1406</c>）不存在。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<DirectoryListing>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListFilesRequest request,
        CancellationToken cancellationToken)
    {
        var containerId = request.ContainerId?.Trim();

        if (String.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("容器标识不能为空。", nameof(request));
        }

        var listing = await _files.ListAsync(hostId, containerId, request.Path, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(listing));
    }

    /// <summary>
    /// 下载容器内的单个文件（**二进制流**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">下载请求（容器标识、路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件流（<c>application/octet-stream</c>）。</returns>
    /// <response code="200">下载流已开始。</response>
    /// <response code="400">容器标识为空或路径非法（<c>1000</c> / <c>1405</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或无容器读权限（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、容器（<c>1401</c>）或文件（<c>1406</c>）不存在。</response>
    /// <remarks>
    /// <para><b>为什么不是 ApiResult</b>：这是文件下载，不是业务数据。
    /// 成功时返回二进制流，失败时返回 JSON 的 <c>ApiResult</c>——
    /// 客户端按 <c>Content-Type</c> 分辨即可。</para>
    /// <para><b>为什么不需要下载票据</b>：这条请求带着 Authorization 头（前端用
    /// <c>responseType: 'blob'</c> 发），因此不必像大 tar 那样换一张票据。</para>
    /// </remarks>
    [HttpPost("download")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAsync(
        string hostId,
        [FromBody] DownloadFileRequest request,
        CancellationToken cancellationToken)
    {
        var containerId = request.ContainerId?.Trim();

        if (String.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("容器标识不能为空。", nameof(request));
        }

        var (stream, fileName, _) = await _files
            .OpenFileAsync(hostId, containerId, request.Path, cancellationToken)
            .ConfigureAwait(false);

        // 文件可能很大，但这里是**另一条**通道：调用方是本平台的前端（带 Bearer 头），
        // 不是浏览器原生导航，因此读出来的流会被框架逐块刷出去。
        return this.File(stream, "application/octet-stream", fileName);
    }

    /// <summary>
    /// 上传单个文件到容器内的一个目录（**multipart/form-data**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="directoryPath">目标目录（省略为根目录）。</param>
    /// <param name="file">要上传的文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为写入的字节数。</returns>
    /// <response code="200">写入成功。</response>
    /// <response code="400">缺少文件或路径非法（<c>1000</c> / <c>1405</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">
    /// 无权访问该主机（<c>1501</c>）、无容器写权限（<c>1502</c>）或写入已被禁用（<c>1409</c>）。
    /// </response>
    /// <response code="404">主机（<c>1300</c>）或容器（<c>1401</c>）不存在。</response>
    /// <response code="413">上传体积超过上限（<c>1407</c>）。</response>
    /// <remarks>
    /// <para><b>为什么是「单个文件」而不是「整个目录」</b>：目录上传要处理多级路径、
    /// 空目录与覆盖语义，而那些都可以用「先建目录（后续迭代）再逐个文件上传」拼出来。
    /// 先只给单个文件，能把路径校验与体积上限这两件事做扎实。</para>
    /// <para><b>为什么文件名从表单里取</b>：<c>IFormFile.FileName</c> 是<b>用户可控</b>的，
    /// 可能带路径分隔符或 <c>..</c>——服务端会重新校验它。</para>
    /// </remarks>
    [HttpPost("write")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(typeof(ApiResult<long>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> WriteAsync(
        string hostId,
        [FromForm] string? containerId,
        [FromForm] string? directoryPath,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("容器标识不能为空。", nameof(containerId));
        }

        if (file is null || file.Length == 0)
        {
            throw new ArgumentException("上传内容不能为空。", nameof(file));
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            throw new BusinessException(
                ErrorCodes.UploadTooLarge,
                $"上传体积超过上限（{_options.MaxUploadBytes} 字节）");
        }

        await using var stream = file.OpenReadStream();

        var written = await _files
            .WriteFileAsync(hostId, containerId, directoryPath, file.FileName, stream, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(written));
    }
}
