namespace Docker.Manager.Application.Images;

/// <summary>
/// 镜像查询请求。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态。校验统一在 <c>ImageService</c> 里做并给出可读消息。
/// </remarks>
public sealed record ListImagesRequest
{
    /// <summary>是否包含中间层镜像；省略时只列顶层镜像（与 <c>docker images</c> 一致）。</summary>
    public bool? All { get; init; }

    /// <summary>按仓库名 / 标签模糊过滤（不区分大小写）；省略不过滤。</summary>
    public string? Search { get; init; }

    /// <summary>页码（从 0 开始）；省略时按 0 处理。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数；省略时用默认值，超出上限会被夹紧。</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 镜像详情请求。
/// </summary>
public sealed record InspectImageRequest
{
    /// <summary>镜像标识（镜像 ID / 短 ID / 仓库名 / 仓库名:标签）。</summary>
    public string? ImageId { get; init; }
}

/// <summary>
/// 拉取镜像请求。
/// </summary>
public sealed record PullImageRequest
{
    /// <summary>镜像仓库名（如 <c>nginx</c>、<c>ghcr.io/org/app</c>）；必填。</summary>
    public string? Image { get; init; }

    /// <summary>标签；省略或为空时取 <c>latest</c>。</summary>
    public string? Tag { get; init; }

    /// <summary>目标平台（如 <c>linux/amd64</c>）；省略时由守护进程按主机架构选择。</summary>
    public string? Platform { get; init; }
}

/// <summary>
/// 删除镜像请求。
/// </summary>
public sealed record RemoveImageRequest
{
    /// <summary>镜像标识（镜像 ID / 短 ID / 仓库名 / 仓库名:标签）。</summary>
    public string? ImageId { get; init; }

    /// <summary>是否强制删除（即便被容器引用或存在子镜像）；省略为 <see langword="false"/>。</summary>
    public bool? Force { get; init; }
}
