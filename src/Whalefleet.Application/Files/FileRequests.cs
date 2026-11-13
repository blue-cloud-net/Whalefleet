namespace Whalefleet.Application.Files;

/// <summary>
/// 列举目录的请求。
/// </summary>
public sealed record ListFilesRequest
{
    /// <summary>
    /// 容器标识（容器文件浏览用）。
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// 卷名（卷文件浏览用）。
    /// </summary>
    public string? VolumeName { get; init; }

    /// <summary>
    /// 目录路径（省略或空串表示根目录）。
    /// </summary>
    public string? Path { get; init; }
}

/// <summary>
/// 下载单个文件的请求。
/// </summary>
public sealed record DownloadFileRequest
{
    /// <summary>
    /// 容器标识（容器文件浏览用）。
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// 卷名（卷文件浏览用）。
    /// </summary>
    public string? VolumeName { get; init; }

    /// <summary>
    /// 文件路径。
    /// </summary>
    public string? Path { get; init; }
}
