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

/// <summary>
/// 批量删除镜像请求。
/// </summary>
public sealed record BatchRemoveImagesRequest
{
    /// <summary>要删除的镜像标识列表（ID / 短 ID / 仓库名 / 仓库名:标签）；必填且不能为空。</summary>
    public IReadOnlyList<string>? ImageIds { get; init; }

    /// <summary>是否强制删除（即便被容器引用或存在子镜像）；省略为 <see langword="false"/>。</summary>
    public bool? Force { get; init; }
}

/// <summary>
/// 给镜像打标签请求（对应 <c>docker tag</c>）。
/// </summary>
/// <remarks>
/// <para><b>tag 不复制镜像</b>：它只是给<b>同一个镜像 ID</b> 再加一个引用。
/// 因此「删掉这个标签」与「删掉这个镜像」是两件事——前者只解绑，后者在没有其他引用时才会删掉本体。
/// 这一点在界面上必须写清楚，否则用户会以为打标签是「复制了一份」，
/// 删副本时把本体一起删掉。</para>
/// </remarks>
public sealed record TagImageRequest
{
    /// <summary>源镜像标识（镜像 ID / 短 ID / 仓库名 / 仓库名:标签）；必填。</summary>
    public string? ImageId { get; init; }

    /// <summary>目标仓库名（如 <c>registry.example.com/team/app</c>）；必填。</summary>
    /// <remarks>不带标签——标签由 <see cref="Tag"/> 单独给，避免 <c>nginx:1.27:alpine</c> 那类拼接歧义。</remarks>
    public string? Repository { get; init; }

    /// <summary>目标标签（如 <c>v1.2.3</c> / <c>latest</c>）；省略时取 <c>latest</c>。</summary>
    public string? Tag { get; init; }
}

/// <summary>
/// 构建镜像请求。
/// </summary>
/// <remarks>
/// <para><b>构建上下文是已打包好的 tar</b>（<c>docker build -</c> 里的那个 <c>-</c>）。
/// 本版<b>不</b>接受多文件上传后由服务端打包：那意味着要在服务端实现一套 tar 写入器，
/// 并给它配套路径穿越防护（tar 条目名可含 <c>../</c>），而收益仅是界面少一步。
/// 用户用 <c>tar -cf context.tar .</c> 一次，比界面上的文件选择器更快。</para>
/// <para><b>构建进度本版一次性返回</b>：与现有 <c>pull</c> 同一形态。
/// 实时进度推送随 <c>0.5.0</c> 的 SignalR 一起改。</para>
/// </remarks>
public sealed record BuildImageRequest
{
    /// <summary>构建上下文 tar 的字节流（由 Api 层从 multipart 上传中打开）。</summary>
    public Stream? Context { get; init; }

    /// <summary>构建上下文 tar 的长度（字节）；用于拼 Content-Length。</summary>
    public long ContextLength { get; init; }

    /// <summary>构建产物的镜像引用（仓库名 + 可选标签，如 <c>myapp:v1</c>）；必填。</summary>
    public string? Tag { get; init; }

    /// <summary>tar 包内的 Dockerfile 路径；省略时为 <c>Dockerfile</c>（与 Docker 一致）。</summary>
    public string? Dockerfile { get; init; }

    /// <summary>构建参数（<c>ARG</c>）；键值都不得为空。</summary>
    public IReadOnlyDictionary<string, string>? BuildArgs { get; init; }

    /// <summary>镜像标签（<c>LABEL</c>）。</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>多阶段构建的目标阶段（<c>--target</c>）；省略则构建最终阶段。</summary>
    public string? Target { get; init; }

    /// <summary>目标平台（如 <c>linux/amd64</c>）；省略时按主机架构。</summary>
    public string? Platform { get; init; }

    /// <summary>是否不使用构建缓存（<c>--no-cache</c>）；省略为 <see langword="false"/>。</summary>
    public bool? NoCache { get; init; }
}

/// <summary>
/// 推送镜像到仓库 的请求。
/// </summary>
public sealed record PushImageRequest
{
    /// <summary>要推送的镜像（ID / 仓库名 / 仓库名:标签）；必填。</summary>
    public string? Image { get; init; }

    /// <summary>
    /// 仓库配置主键；省略时<b>按镜像引用里的地址段自动匹配</b>。
    /// </summary>
    /// <remarks>
    /// 自动匹配让「先在页面上选了仓库，再去镜像列表点 push」能成立；
    /// 显式给主键则允许「本地标签不带仓库前缀，但就是要推到内网 Harbor」这种场景。
    /// </remarks>
    public Guid? RepositoryId { get; init; }

    /// <summary>目标平台；省略时不带该参数。</summary>
    public string? Platform { get; init; }
}
