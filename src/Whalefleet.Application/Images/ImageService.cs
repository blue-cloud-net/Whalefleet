using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像用例（列表 / 详情 / 拉取 / 删除），对应 <c>/api/v1/hosts/{hostId}/images/*</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么拉取要自己看进度消息</b>：Docker 拉取失败有两条路径。「镜像 / 仓库不存在」
/// 会让 SDK 抛异常（走 <c>DockerErrorTranslator</c> 的 1401 分支）；但「仓库不让你拉」「网络中断」
/// 则 <b>不抛异常</b>——HTTP 层仍然是 200，错误只在进度流里带一条 <c>error</c> 消息。
/// 只等 <c>await CreateImageAsync(...)</c> 返回的话，后一类会被报成「成功但什么也没拉到」，
/// 那比报错难查得多。因此 <c>PullCollector</c> 把进度消息当作<b>必须检查的数据源</b>，
/// 而不只是日志素材。</para>
/// <para><b>主机校验不在这里</b>：主机是否存在（<c>1300</c>）与可见性（<c>1501</c>）由
/// Api 层的 <c>CurrentHostMiddleware</c> 在进入本服务前完成。</para>
/// <para><b>外部响应按可空处理</b>：SDK 模型把很多字段声明为非空，但它们的值来自另一个进程的 JSON。
/// 缺字段时抛 <see cref="NullReferenceException"/> 会被兜底成 <c>9999</c>/500，而这明明是「对端数据不全」，
/// 因此所有映射都按可空写，缺失就退化成空值而不是崩掉。</para>
/// <para><b>日志纪律</b>：只记主机标识、镜像引用与条数，<b>不</b>记镜像的 <c>ENV</c> / <c>LABEL</c>
/// （构建参数里常有令牌与密码——记进来等于把凭据写进日志文件）。</para>
/// </remarks>
public sealed class ImageService
{
    /// <summary>列表默认每页条数。</summary>
    private const int DefaultPageSize = 20;

    /// <summary>列表每页条数上限。</summary>
    /// <remarks>上限用于挡住「pageSize=100000」这种把整台宿主机镜像一次吐出去的要求，避免响应体失控。</remarks>
    private const int MaxPageSize = 100;

    /// <summary>删除响应里表示「镜像本体被删除」的键。</summary>
    private const string DeletedKey = "Deleted";

    /// <summary>删除响应里表示「标签被解绑」的键。</summary>
    private const string UntaggedKey = "Untagged";

    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<ImageService> _logger;

    /// <summary>
    /// 初始化镜像用例。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ImageService(IDockerHostAccessor accessor, ILogger<ImageService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// 列出主机上的本地镜像。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（是否含中间层、搜索、分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页数据与总数。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）、已停用（<c>1302</c>）、Docker 调用失败（<c>1402</c>）或超时（<c>1403</c>）。
    /// </exception>
    public async Task<ImageListResult> ListAsync(
        string hostId,
        ListImagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);
        var all = request.All ?? false;

        var images = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.ListImagesAsync(new ImagesListParameters { All = all }, cancellationToken),
            "镜像列表",
            cancellationToken).ConfigureAwait(false);

        // 顺序沿用守护进程的返回顺序（创建时间降序）：它是稳定的，额外重排只会让「翻页时看到的
        // 东西变了」更难解释。客户端**显式**指定排序字段时才重排——那时顺序是用户要的，
        // 反而要说明白是客户端排的；没指定时保持原样。
        var filtered = Sort(Filter(images, request.Search), request.SortBy, request.SortDir);

        var pageIndex = Math.Max(request.PageIndex ?? 0, 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        _logger.LogDebug(
            "主机 {HostId} 镜像列表：共 {Total} 个（含中间层={All}，搜索={Search}，排序={SortBy}/{SortDir}），返回第 {PageIndex} 页（每页 {PageSize}）",
            hostId,
            filtered.Count,
            all,
            string.IsNullOrWhiteSpace(request.Search) ? "无" : request.Search.Trim(),
            request.SortBy ?? "默认",
            request.SortDir ?? "desc",
            pageIndex,
            pageSize);

        return new ImageListResult(
            [.. filtered.Skip(pageIndex * pageSize).Take(pageSize).Select(MapSummary)],
            filtered.Count,
            pageIndex,
            pageSize);
    }

    /// <summary>
    /// 查询镜像详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（镜像标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>镜像详情投影。</returns>
    /// <exception cref="BusinessException">
    /// 镜像标识为空（<c>1000</c>）、镜像不存在（<c>1401</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<ImageDetail> InspectAsync(
        string hostId,
        InspectImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var imageId = RequireImageId(request.ImageId);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var image = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.InspectImageAsync(imageId, cancellationToken),
            $"镜像 {imageId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("主机 {HostId} 查询镜像 {ImageId} 详情", hostId, imageId);

        return MapDetail(image);
    }

    /// <summary>
    /// 拉取镜像（一次性等待完成，不做实时进度推送）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">拉取请求（仓库名、标签、平台）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>拉取后的镜像 ID、大小与标签。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）、拉取失败（<c>1402</c>，消息里带守护进程给的原因）、
    /// 主机不可达（<c>1301</c>）或超时（<c>1403</c>）。
    /// </exception>
    /// <remarks>
    /// <b>实时进度推到 0.4.0</b>：这里只把进度写进日志，客户端在响应回来之前只能转圈。
    /// 那时连同列表页与详情页一起改用 SignalR 推送。
    /// </remarks>
    public async Task<ImagePullResult> PullAsync(
        string hostId,
        PullImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var (image, tag) = ResolveImageReference(request.Image, request.Tag);
        var reference = $"{image}:{tag}";
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ImagesCreateParameters
        {
            FromImage = image,
            Tag = tag,
            Platform = string.IsNullOrWhiteSpace(request.Platform) ? null : request.Platform.Trim(),
        };

        var collector = new PullCollector(_logger, hostId, reference);

        _logger.LogInformation("开始拉取镜像 {Reference}", reference);

        // 认证参数传 null：0.3.0 之前没有 Registry 配置，拉的都是公开镜像。
        // 不能传 new AuthConfig() —— 那会被当成「空用户名 + 空密码」去认证公开仓库并失败。
        await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.CreateImageAsync(parameters, authConfig: null, collector, cancellationToken),
            $"拉取镜像 {reference}",
            cancellationToken).ConfigureAwait(false);

        // 必须查进度消息：Docker 拉取失败有两类，HTTP 层都可能不是失败——
        // 「镜像 / 仓库不存在」由 SDK 抛异常（走翻译器的 1401），
        // 而「仓库不让你拉」「网络中断」只在进度流里带一条 error，SDK 毫不知情。
        // 不检查这一条，后一类就会被报成「成功但什么也没拉到」。
        collector.ThrowIfFailed();

        // 验收标准要求「返回镜像 ID 与大小」：这两项只能从 inspect 拿。这次 inspect 顺带确认
        // 镜像确实落盘了（拉取流结束不等于所有层都写完了）。
        var inspected = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.InspectImageAsync(reference, cancellationToken),
            $"镜像 {reference}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "拉取镜像 {Reference} 完成：{ImageId}，{Size} 字节（{Layers} 层）",
            reference,
            inspected.ID,
            inspected.Size,
            collector.LayerCount);

        return new ImagePullResult(
            inspected.ID,
            reference,
            inspected.Size,
            MapReferences(inspected.RepoTags));
    }

    /// <summary>
    /// 删除本地镜像。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（镜像标识、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被解绑的标签与被删除的镜像 ID。</returns>
    /// <exception cref="BusinessException">
    /// 镜像标识为空（<c>1000</c>）、镜像不存在（<c>1401</c>）、
    /// 被容器引用或存在子镜像（<c>1404</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>永远不传 <c>NoPrune</c></b>：那会连带删掉所有未被引用的父层镜像，而本项目没有暴露任何
    /// 「清理悬空层」的入口——删镜像顺手清了别的镜像是不可预期的数据损失。
    /// </remarks>
    public async Task<ImageRemovedResult> RemoveAsync(
        string hostId,
        RemoveImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var imageId = RequireImageId(request.ImageId);
        var force = request.Force ?? false;
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        return await this.RemoveAsync(client, imageId, force, hostId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量删除镜像（逐条汇报失败，**部分成功也算成功**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">批量删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>汇总结果（不含每张镜像解绑了哪些标签——批量场景下逐条标签明细会让响应体失控）。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）或已停用（<c>1302</c>）；列表为空 / 超限时 <c>1000</c>。
    /// </exception>
    /// <remarks>
    /// <para><b>与容器批量删除同一套骨架</b>：客户端取一次、串行、逐条收失败。
    /// 两处的差异只有镜像的删除需要解析响应（解绑了哪些标签），
    /// 而那个明细在批量场景下价值不大——用户能看见的只有列表刷新后的结果。</para>
    /// <para><b>为什么仍然串行</b>：镜像删除会连带解绑标签并更新引用计数，
    /// 并发时同一个基础镜像可能被两个条目同时处理，失败原因将无法解释。</para>
    /// </remarks>
    public async Task<BatchOperationResult> BatchRemoveAsync(
        string hostId,
        BatchRemoveImagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var imageIds = BatchRequest.NormalizeIds(request.ImageIds, nameof(request.ImageIds));
        var force = request.Force ?? false;

        // 放在循环外：主机不可达是整批的失败，逐条汇报「100 个都失败了」对用户毫无帮助
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var failures = new List<BatchOperationFailure>();

        foreach (var imageId in imageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await this.RemoveAsync(client, imageId, force, hostId, cancellationToken).ConfigureAwait(false);
            }
            catch (BusinessException exception)
            {
                failures.Add(new BatchOperationFailure(imageId, exception.Code, exception.Message));
            }
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "主机 {HostId} 批量删除镜像：成功 {Succeeded}/{Requested}，失败 {Failed} 条（强制={Force}）",
                hostId,
                imageIds.Count - failures.Count,
                imageIds.Count,
                failures.Count,
                force);
        }
        else
        {
            _logger.LogInformation(
                "主机 {HostId} 批量删除镜像：全部 {Count} 个成功（强制={Force}）",
                hostId,
                imageIds.Count,
                force);
        }

        return new BatchOperationResult(imageIds.Count, imageIds.Count - failures.Count, failures);
    }

    /// <summary>
    /// 用已有客户端删除单个镜像（单条与批量端点共用）。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="imageId">镜像标识。</param>
    /// <param name="force">是否强制删除。</param>
    /// <param name="hostId">主机标识（仅用于日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被解绑的标签与被删除的镜像 ID。</returns>
    private async Task<ImageRemovedResult> RemoveAsync(
        IDockerClient client,
        string imageId,
        bool force,
        string hostId,
        CancellationToken cancellationToken)
    {
        var responses = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.DeleteImageAsync(
                imageId,
                new ImageDeleteParameters { Force = force, NoPrune = false },
                cancellationToken),
            $"镜像 {imageId}",
            cancellationToken).ConfigureAwait(false);

        // 守护进程对每条处理结果各回一个字典：键是 "deleted" 或 "untagged"，值是对应的 ID / 标签。
        var deleted = Extract(responses, DeletedKey);
        var untagged = Extract(responses, UntaggedKey);

        _logger.LogInformation(
            "主机 {HostId} 删除镜像 {ImageId}（强制={Force}）：解绑 {UntaggedCount} 个标签，删除 {DeletedCount} 个镜像",
            hostId,
            imageId,
            force,
            untagged.Length,
            deleted.Length);

        return new ImageRemovedResult(untagged, deleted);
    }

    /// <summary>
    /// 从删除响应里取出某个键对应的全部值。
    /// </summary>
    /// <param name="responses">守护进程返回的字典列表。</param>
    /// <param name="key">目标键（<c>Deleted</c> 或 <c>Untagged</c>）。</param>
    /// <returns>该键出现过的全部值。</returns>
    /// <remarks>
    /// ⚠️ 键名是<b>首字母大写</b>的 <c>Deleted</c> / <c>Untagged</c>（SDK 的 JSON 反序列化保留原样）。
    /// 按小写匹配不会报错，只会静默得到空列表——于是「删掉了」看起来像「什么也没发生」。
    /// </remarks>
    private static string[] Extract(IList<IDictionary<string, string>> responses, string key) =>
        [.. responses
            .Where(response => response.ContainsKey(key))
            .Select(response => response[key])
            .Where(value => !string.IsNullOrWhiteSpace(value))];

    /// <summary>
    /// 按搜索关键字过滤镜像。
    /// </summary>
    /// <param name="images">全部镜像。</param>
    /// <param name="search">搜索关键字；为空时原样返回。</param>
    /// <returns>命中的镜像。</returns>
    private static IReadOnlyList<ImagesListResponse> Filter(
        IList<ImagesListResponse> images,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [.. images];
        }

        var keyword = search.Trim();

        return
        [
            .. images.Where(image =>
                Contains(image.ID, keyword)
                || image.RepoTags?.Any(tag => Contains(tag, keyword)) == true
                || image.RepoDigests?.Any(digest => Contains(digest, keyword)) == true),
        ];
    }

    /// <summary>
    /// 按请求指定的字段排序。
    /// </summary>
    /// <param name="images">过滤后的镜像。</param>
    /// <param name="sortBy">排序字段（见 <see cref="ImageSortField"/>）；为空表示不排序。</param>
    /// <param name="sortDir">排序方向；<c>asc</c> 为升序，其余按降序。</param>
    /// <returns>排序后的镜像；未指定字段时原样返回。</returns>
    /// <remarks>
    /// <b>同值项用镜像 ID 兜底排序</b>：只看创建时间时，一批 <c>docker import</c> 进来的镜像
    /// 时间戳常常完全相同，而 <see cref="List{T}"/> 是不稳定排序——同样两个镜像，
    /// 翻到第二页可能出现在不同位置，用户会以为有镜像凭空消失了。
    /// ID 唯一，拿它当次级键能让顺序可复现。
    /// <br/>
    /// <b>仓库名取首个标签</b>：悬挂镜像（无标签）的 <c>RepoTags</c> 为空，
    /// 直接取 <c>[0]</c> 会抛 <see cref="ArgumentOutOfRangeException"/>。
    /// 空列表按空串参与比较，排在有名字的镜像后面（升序）/ 前面（降序）——任一都是可预期的，
    /// 抛异常则不是。
    /// </remarks>
    private static IReadOnlyList<ImagesListResponse> Sort(
        IReadOnlyList<ImagesListResponse> images,
        string? sortBy,
        string? sortDir)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return images;
        }

        var field = sortBy.Trim();
        var descending = !string.Equals(sortDir?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);

        // 排序键 + 次级键（ID）一起比，比"先 OrderBy 再 ThenBy"少一趟枚举
        Comparison<ImagesListResponse> comparison = field switch
        {
            ImageSortField.Repository => (x, y) =>
                Compare(RepositoryKey(x), RepositoryKey(y)),
            ImageSortField.Size => (x, y) => x.Size.CompareTo(y.Size),
            ImageSortField.UsedBy => (x, y) => CountUsedBy(x).CompareTo(CountUsedBy(y)),
            // 默认分支（含未知的未来字段）：退回创建时间，而不是拒绝请求
            _ => (x, y) => CreatedKey(x).CompareTo(CreatedKey(y)),
        };

        var sorted = images
            .Select((image, index) => (Image: image, Index: index))
            .ToList();

        sorted.Sort((x, y) =>
        {
            var result = descending ? comparison(y.Image, x.Image) : comparison(x.Image, y.Image);

            // 同值时用 ID 兜底，保证顺序稳定；ID 也相同（理论上不会）时回退到原下标
            if (result != 0)
            {
                var byId = string.CompareOrdinal(x.Image.ID ?? string.Empty, y.Image.ID ?? string.Empty);

                return byId != 0 ? byId : x.Index.CompareTo(y.Index);
            }

            return x.Index.CompareTo(y.Index);
        });

        return [.. sorted.Select(item => item.Image)];
    }

    /// <summary>仓库名排序键：取首个标签，悬挂镜像（无标签）用空串。</summary>
    private static string RepositoryKey(ImagesListResponse image) =>
        image.RepoTags?.FirstOrDefault() ?? string.Empty;

    /// <summary>创建时间排序键。SDK 的 <c>Created</c> 是非空值类型，直接取用。</summary>
    private static DateTime CreatedKey(ImagesListResponse image) => image.Created;

    /// <summary>被引用容器数：取容器计数，未提供时按 0。</summary>
    private static long CountUsedBy(ImagesListResponse image) => image.Containers;

    /// <summary>字符串比较：按序数比较，避免受当前区域设置影响（大小写排序会随系统语言变化）。</summary>
    private static int Compare(string? x, string? y) =>
        string.CompareOrdinal(x ?? string.Empty, y ?? string.Empty);

    /// <summary>
    /// 镜像标识必填校验。
    /// </summary>
    /// <param name="imageId">请求里的镜像标识。</param>
    /// <returns>去空白后的镜像标识。</returns>
    /// <exception cref="BusinessException">为空时抛 <c>1000</c>。</exception>
    private static string RequireImageId(string? imageId) =>
        string.IsNullOrWhiteSpace(imageId)
            ? throw new BusinessException(ErrorCodes.ValidationFailed, "imageId 不能为空")
            : imageId.Trim();

    /// <summary>
    /// 把「仓库名 + 标签」解析成完整的镜像引用。
    /// </summary>
    /// <param name="image">请求里的仓库名。</param>
    /// <param name="tag">请求里的标签。</param>
    /// <returns>去空白后的仓库名与标签。</returns>
    /// <exception cref="BusinessException">
    /// 仓库名为空（<c>1000</c>），或仓库名已含标签而同时又传了标签（<c>1000</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么「已含标签又传标签」要报错</b>：Docker 会把 <c>nginx:1.27</c> 加 <c>alpine</c>
    /// 拼成 <c>nginx:1.27:alpine</c>（并拉到一个谁都没要求过的镜像）。静默接受会让用户拿到
    /// 一个「和请求长得不一样」的镜像，而真正想要的镜像并没有被拉下来。
    /// </remarks>
    private static (string Image, string Tag) ResolveImageReference(string? image, string? tag)
    {
        var name = RequireImageId(image).TrimEnd(':');
        var requestedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();

        if (!HasTag(name))
        {
            return (name, requestedTag ?? "latest");
        }

        return requestedTag is null
            ? (name, "latest")
            : throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"image 已含标签（{name}），不能再单独传 tag（{requestedTag}）。请只保留其中一种写法");
    }

    /// <summary>
    /// 判断仓库名里是否带标签。
    /// </summary>
    /// <param name="name">仓库名（如 <c>nginx</c> / <c>nginx:1.27</c> / <c>registry:5000/app</c>）。</param>
    /// <returns>带标签返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>判据是「冒号是否在最后一个斜杠之后」</b>，与 Docker 自己的一致：端口号必须出现在
    /// 仓库路径<b>之前</b>（<c>registry:5000/app</c>），因此 <c>registry:5000/app</c> 的冒号在斜杠左边，
    /// 是端口；而 <c>nginx:1.27</c> 没有斜杠，冒号在斜杠「之后」（即 -1），是标签。
    /// <para>⚠️ <b>不能用「冒号后是不是纯数字」来判断</b>：标签名允许纯数字（<c>nginx:1</c> 合法），
    /// 那种规则会把 <c>nginx:1.27</c> 误判成「带端口的仓库」，再拼上一个 tag 就成了
    /// <c>nginx:1.27:alpine</c>——一个谁都没要求过的镜像。</para>
    /// </remarks>
    private static bool HasTag(string name)
    {
        var colon = name.LastIndexOf(':');

        return colon > name.LastIndexOf('/');
    }

    /// <summary>
    /// 不区分大小写的包含判断。
    /// </summary>
    /// <param name="haystack">被搜索的文本（可能为 <see langword="null"/>）。</param>
    /// <param name="needle">搜索关键字。</param>
    /// <returns>包含时返回 <see langword="true"/>。</returns>
    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 过滤掉 Docker 的 <c>&lt;none&gt;</c> 占位标签。
    /// </summary>
    /// <param name="references">SDK 返回的引用列表。</param>
    /// <returns>去占位、去重后的引用列表。</returns>
    /// <remarks>
    /// Docker 用 <c>&lt;none&gt;</c> 占位表示「这个镜像没有标签」（构建中间层就是这种）。
    /// 原样返回会让界面上出现一行叫「&lt;none&gt;:&lt;none&gt;」的镜像；过滤后它只是空列表，
    /// 界面可以显示成「&lt;none&gt;」并仍允许按 ID 操作。
    /// </remarks>
    private static IReadOnlyList<string> MapReferences(IList<string>? references) =>
        references is null
            ? []
            : [.. references
                .Where(reference => !string.IsNullOrWhiteSpace(reference)
                    && !reference.Contains("<none>", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// 把列表项投影成对外结构。
    /// </summary>
    /// <param name="source">SDK 镜像项。</param>
    /// <returns>列表项投影。</returns>
    private static ImageSummary MapSummary(ImagesListResponse source) => new(
        Id: source.ID,
        RepoTags: MapReferences(source.RepoTags),
        RepoDigests: MapReferences(source.RepoDigests),
        Created: ToUtc(source.Created),
        Size: source.Size,
        SharedSize: source.SharedSize,
        ContainerCount: source.Containers);

    /// <summary>
    /// 把 inspect 结果投影成对外结构。
    /// </summary>
    /// <param name="source">SDK 详情。</param>
    /// <returns>详情投影。</returns>
    private static ImageDetail MapDetail(ImageInspectResponse source)
    {
        var config = source.Config;

        return new ImageDetail(
            Id: source.ID,
            RepoTags: MapReferences(source.RepoTags),
            RepoDigests: MapReferences(source.RepoDigests),
            Created: ToUtc(source.Created),
            Size: source.Size,
            Architecture: source.Architecture ?? string.Empty,
            Os: source.Os ?? string.Empty,
            OsVersion: source.OsVersion ?? string.Empty,
            Author: source.Author ?? string.Empty,
            Comment: source.Comment ?? string.Empty,
            Env: config?.Env is null ? [] : [.. config.Env],
            Cmd: config?.Cmd is null ? [] : [.. config.Cmd],
            Entrypoint: config?.Entrypoint is null ? [] : [.. config.Entrypoint],
            ExposedPorts: MapKeys(config?.ExposedPorts),
            Volumes: MapKeys(config?.Volumes),
            Labels: MapLabels(config?.Labels),
            WorkingDir: config?.WorkingDir ?? string.Empty,
            LayerCount: source.RootFS?.Layers?.Count ?? 0);
    }

    /// <summary>
    /// 取字典的键（Docker 的 <c>EXPOSE</c> / <c>VOLUME</c> 值是空的，只有关键有信息）。
    /// </summary>
    /// <param name="source">SDK 字典（值为 <see cref="EmptyStruct"/>）。</param>
    /// <returns>键列表；无内容时为空列表。</returns>
    private static IReadOnlyList<string> MapKeys(IDictionary<string, EmptyStruct>? source) =>
        source is null ? [] : [.. source.Keys];

    /// <summary>
    /// 复制标签字典。
    /// </summary>
    /// <param name="source">SDK 标签字典。</param>
    /// <returns>标签字典；无内容时为空字典。</returns>
    private static Dictionary<string, string> MapLabels(IDictionary<string, string>? source) =>
        source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);

    /// <summary>
    /// 把 SDK 的 <see cref="DateTime"/> 统一成 UTC 时刻。
    /// </summary>
    /// <param name="value">SDK 解析出的时间（inspect 可能缺这个字段）。</param>
    /// <returns>带时区的时刻；缺值时为 <see cref="DateTimeOffset.MinValue"/>。</returns>
    /// <remarks>
    /// Docker 的 JSON 里时间始终是 UTC，但 SDK 反序列化后 <see cref="DateTime.Kind"/> 可能是
    /// <see cref="DateTimeKind.Unspecified"/>（表现为「本地时间」）。按 UTC 解释它是唯一与协议一致的选择。
    /// </remarks>
    private static DateTimeOffset ToUtc(DateTime? value) =>
        value is null
            ? DateTimeOffset.MinValue
            : value.Value.Kind == DateTimeKind.Local
                ? new DateTimeOffset(value.Value)
                : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    /// <summary>
    /// 同步收集拉取进度消息的 <see cref="IProgress{T}"/> 实现。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不用 <c>System.Progress&lt;JSONMessage&gt;</c></b>：它把回调 <c>Post</c> 到线程池，
    /// 于是 <c>await CreateImageAsync(...)</c> 返回时队列里可能还有没执行完的回调——
    /// 「拉取失败」的错误消息会被漏掉（错误检查在 await 之后做），于是失败被报成成功。
    /// 同步实现没有这个时间窗。</para>
    /// <para><b>为什么可以不上锁</b>：SDK 在单条读取循环里顺序回调，不存在并发进入。
    /// 这是依赖库行为的前提，因此写在这里而不是靠猜。</para>
    /// </remarks>
    private sealed class PullCollector : IProgress<JSONMessage>
    {
        private readonly ILogger _logger;
        private readonly string _hostId;
        private readonly string _reference;
        private string? _failure;

        /// <summary>
        /// 初始化进度收集器。
        /// </summary>
        /// <param name="logger">日志（逐层进度记 Debug，完成与失败记 Information / Warning）。</param>
        /// <param name="hostId">主机标识（写进日志便于定位是哪台主机）。</param>
        /// <param name="reference">镜像引用（写进日志便于定位是哪个镜像）。</param>
        public PullCollector(ILogger logger, string hostId, string reference)
        {
            _logger = logger;
            _hostId = hostId;
            _reference = reference;
        }

        /// <summary>已见到的层数（按带层 ID 的进度消息计）。</summary>
        public int LayerCount { get; private set; }

        /// <inheritdoc />
        public void Report(JSONMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            // 先记错误：一旦失败，后续进度消息没有意义，但仍要继续读完（不能提前抛，
            // 那会让 SDK 的连接处于未读完状态）。
            if (message.Error?.Message is { Length: > 0 } error)
            {
                this.RecordFailure(error);
                return;
            }

            var status = message.Status ?? string.Empty;

            if (status.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                this.RecordFailure(status);
                return;
            }

            if (message.ID is { Length: > 0 })
            {
                LayerCount++;
            }

            // Digest 那条是最后一条成功标记；其余逐层进度只进 Debug，避免刷屏。
            if (status.StartsWith("Digest:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "主机 {HostId} 镜像 {Reference} 拉取完成：{Status}",
                    _hostId,
                    _reference,
                    status);

                return;
            }

            _logger.LogDebug(
                "主机 {HostId} 镜像 {Reference}：{LayerId} {Status}",
                _hostId,
                _reference,
                message.ID ?? "-",
                status);
        }

        /// <summary>
        /// 拉取失败时抛出业务异常。
        /// </summary>
        /// <exception cref="BusinessException">进度流里出现过错误时抛 <c>1402</c>。</exception>
        /// <remarks>
        /// 归成 <c>1402</c>/500：能从进度流里读到的失败原因无法判断是「仓库不让你拉」还是
        /// 「网络中断」，但两者都不是「资源不存在」（那是 404，由 SDK 抛异常走 1401）。
        /// 把守护进程给的原因原样带上，调用方与用户都能看到真正的原因。
        /// </remarks>
        public void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                throw new BusinessException(
                    ErrorCodes.DockerApiFailed,
                    $"拉取镜像 {_reference} 失败：{_failure}");
            }
        }

        /// <summary>
        /// 记录第一条错误（后续错误不再覆盖——第一条通常最接近根因）。
        /// </summary>
        /// <param name="reason">错误原因。</param>
        private void RecordFailure(string reason)
        {
            if (_failure is not null)
            {
                return;
            }

            _failure = reason;

            _logger.LogWarning(
                "主机 {HostId} 镜像 {Reference} 拉取失败：{Reason}",
                _hostId,
                _reference,
                reason);
        }
    }
}
