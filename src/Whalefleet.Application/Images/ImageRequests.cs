namespace Whalefleet.Application.Images;

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

    /// <summary>
    /// 排序字段；省略时保持守护进程返回的顺序（创建时间降序）。
    /// </summary>
    /// <remarks>
    /// 取值见 <see cref="ImageSortField"/> 的常量。**未知值一律按创建时间处理**，
    /// 而不是报错——排序字段来自客户端，版本错配时旧客户端传新字段不该让整个列表 400。
    /// </remarks>
    public string? SortBy { get; init; }

    /// <summary>排序方向；<c>desc</c>（或省略）为降序，<c>asc</c> 为升序。</summary>
    /// <remarks>同样不校验：无法识别的值按降序处理，与「省略」行为一致。</remarks>
    public string? SortDir { get; init; }
}

/// <summary>
/// 镜像列表的排序字段取值。
/// </summary>
/// <remarks>
/// 做成常量而不是枚举：请求字段是 <see cref="string"/>（客户端可传任意值），
/// 用 <c>switch</c> 映射成委托表比 <c>Enum.TryParse</c> 更直白，也让「未知值回落」
/// 这条策略在代码里看得见。
/// </remarks>
public static class ImageSortField
{
    /// <summary>仓库名（按 <c>docker images</c> 的默认展示列）。</summary>
    public const string Repository = "repository";

    /// <summary>镜像大小（字节）。</summary>
    public const string Size = "size";

    /// <summary>创建时间。</summary>
    public const string Created = "created";

    /// <summary>被容器引用的数量。</summary>
    public const string UsedBy = "usedBy";

    /// <summary>默认字段：等价于守护进程自己给的创建时间降序。</summary>
    public const string Default = Created;
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
