using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Common;
using Whalefleet.Application.Files;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 卷文件浏览端点（0.14.0，**主机作用域**）。
/// </summary>
/// <remarks>
/// <para><b>实现细节对调用方透明</b>：卷没有「读内容」的 Docker 接口，
/// 服务端会为每次请求建一个<b>一次性辅助容器</b>、把卷挂进去、用完立刻删掉。
/// 接口形态因此与容器文件浏览完全一致——前端不必知道底下多了一个容器。</para>
/// <para><b>代价写在文档里</b>：每次浏览都要建 / 删一个容器，
/// 且需要宿主机上存在辅助镜像（<c>Files:HelperImage</c>）。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/volumes/files")]
[Authorize]
public sealed class VolumeFilesController : ControllerBase
{
    private readonly VolumeFileService _files;
    private readonly FilesOptions _options;

    /// <summary>
    /// 初始化卷文件浏览控制器。
    /// </summary>
    /// <param name="files">卷文件浏览用例。</param>
    /// <param name="options">文件传输配置。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public VolumeFilesController(VolumeFileService files, IOptions<FilesOptions> options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);

        _files = files;
        _options = options.Value;
    }

    /// <summary>
    /// 列举卷内的一个目录。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">列举请求（卷名、路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为目录清单。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">卷名为空或路径非法（<c>1000</c> / <c>1405</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或无卷读权限（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、卷（<c>1401</c>）或路径（<c>1406</c>）不存在。</response>
    /// <response code="500">辅助镜像不可用（<c>1402</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<DirectoryListing>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListFilesRequest request,
        CancellationToken cancellationToken)
    {
        var volumeName = request.VolumeName?.Trim();

        if (String.IsNullOrWhiteSpace(volumeName))
        {
            throw new ArgumentException("卷名不能为空。", nameof(request));
        }

        var listing = await _files.ListAsync(hostId, volumeName, request.Path, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(listing));
    }

    /// <summary>
    /// 下载卷内的单个文件（**二进制流**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">下载请求（卷名、路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件流（<c>application/octet-stream</c>）。</returns>
    /// <response code="200">下载流已开始。</response>
    /// <response code="400">卷名为空或路径非法（<c>1000</c> / <c>1405</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或无卷读权限（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）、卷（<c>1401</c>）或文件（<c>1406</c>）不存在。</response>
    /// <remarks>
    /// <b>内容会先被完整读进内存</b>：辅助容器必须在读完之前保持存在，
    /// 而「边读边删容器」做不到。因此超大文件不适合走这条路——
    /// 文档里要写明这一点。
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
        var volumeName = request.VolumeName?.Trim();

        if (String.IsNullOrWhiteSpace(volumeName))
        {
            throw new ArgumentException("卷名不能为空。", nameof(request));
        }

        var (stream, fileName, _) = await _files
            .OpenFileAsync(hostId, volumeName, request.Path, cancellationToken)
            .ConfigureAwait(false);

        return this.File(stream, "application/octet-stream", fileName);
    }

    /// <summary>
    /// 上传单个文件到卷内的一个目录（**multipart/form-data**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="volumeName">卷名。</param>
    /// <param name="directoryPath">目标目录（省略为卷根）。</param>
    /// <param name="file">要上传的文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为写入的字节数。</returns>
    /// <response code="200">写入成功。</response>
    /// <response code="400">缺少文件或路径非法（<c>1000</c> / <c>1405</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">
    /// 无权访问该主机（<c>1501</c>）、无卷写权限（<c>1502</c>）或写入已被禁用（<c>1409</c>）。
    /// </response>
    /// <response code="404">主机（<c>1300</c>）或卷（<c>1401</c>）不存在。</response>
    /// <response code="413">上传体积超过上限（<c>1407</c>）。</response>
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
        [FromForm] string? volumeName,
        [FromForm] string? directoryPath,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        var volume = volumeName?.Trim();

        if (String.IsNullOrWhiteSpace(volume))
        {
            throw new ArgumentException("卷名不能为空。", nameof(volumeName));
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
            .WriteFileAsync(hostId, volume, directoryPath, file.FileName, stream, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(written));
    }
}
