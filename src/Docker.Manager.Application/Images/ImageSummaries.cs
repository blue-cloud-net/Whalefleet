namespace Docker.Manager.Application.Images;

/// <summary>
/// 镜像列表项。
/// </summary>
/// <param name="Id">镜像完整 ID（<c>sha256:…</c>）。</param>
/// <param name="RepoTags">仓库标签（如 <c>nginx:latest</c>）；悬挂镜像为空列表。</param>
/// <param name="RepoDigests">内容寻址摘要（如 <c>nginx@sha256:…</c>）。</param>
/// <param name="Created">创建时间（UTC）。</param>
/// <param name="Size">镜像大小（字节）。</param>
/// <param name="SharedSize">与其他镜像共享的字节数。</param>
/// <param name="ContainerCount">引用该镜像的容器数。</param>
/// <remarks>
/// <para><b>为什么逐字段挑选而不是直接序列化 SDK 的 <c>ImagesListResponse</c></b>：
/// 那个对象带着 <c>Descriptor</c> / <c>ParentID</c> / <c>Manifests</c> 等列表页用不到的东西，
/// 而且其中多数只在新版 Docker 才有——原样透传等于让「响应里有什么」随守护进程版本变化。</para>
/// <para><b>悬挂镜像（<c>&lt;none&gt;:&lt;none&gt;</c>）的标签被过滤掉</b>：Docker 用
/// <c>&lt;none&gt;</c> 占位表示「这个镜像没有标签」（例如构建中间层）。原样返回会让界面上出现
/// 一行叫「&lt;none&gt;:&lt;none&gt;」的镜像；过滤后它只是 <c>repoTags: []</c>，
/// 界面可以据此显示为「&lt;none&gt;」并仍允许按 ID 操作。</para>
/// </remarks>
public sealed record ImageSummary(
    string Id,
    IReadOnlyList<string> RepoTags,
    IReadOnlyList<string> RepoDigests,
    DateTimeOffset Created,
    long Size,
    long SharedSize,
    long ContainerCount);

/// <summary>
/// 镜像列表的分页结果。
/// </summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数（分页前，已含 <c>search</c> 过滤）。</param>
/// <param name="PageIndex">当前页码（从 0 开始）。</param>
/// <param name="PageSize">每页条数（夹紧后的实际值）。</param>
/// <remarks>
/// 分页在服务端内存里做：Docker API 不提供镜像列表的分页参数。单台守护进程上镜像数通常是几十到几百，
/// 一次拉全量再切片完全够用。把夹紧后的页码与条数一并返回，调用方传 9999 时能看出服务端实际用了多少。
/// </remarks>
public sealed record ImageListResult(
    IReadOnlyList<ImageSummary> Items,
    int Total,
    int PageIndex,
    int PageSize);

/// <summary>
/// 镜像详情（inspect 的<b>精选</b>投影）。
/// </summary>
/// <param name="Id">镜像完整 ID。</param>
/// <param name="RepoTags">仓库标签。</param>
/// <param name="RepoDigests">内容寻址摘要。</param>
/// <param name="Created">创建时间（UTC）。</param>
/// <param name="Size">镜像大小（字节）。</param>
/// <param name="Architecture">CPU 架构。</param>
/// <param name="Os">操作系统。</param>
/// <param name="OsVersion">操作系统版本（仅 Windows 镜像有值）。</param>
/// <param name="Author">构建者（取自旧版 <c>MAINTAINER</c>；新版已弃用，通常为空）。</param>
/// <param name="Comment">镜像注释。</param>
/// <param name="Env">环境变量（<c>KEY=VALUE</c>）。</param>
/// <param name="Cmd">默认启动命令。</param>
/// <param name="Entrypoint">默认入口点。</param>
/// <param name="ExposedPorts">镜像声明暴露的端口（<c>Dockerfile</c> 的 <c>EXPOSE</c>，形如 <c>80/tcp</c>）。</param>
/// <param name="Volumes">镜像声明的卷（<c>Dockerfile</c> 的 <c>VOLUME</c>，形如 <c>/var/cache/nginx</c>）。</param>
/// <param name="Labels">标签。</param>
/// <param name="WorkingDir">工作目录。</param>
/// <param name="LayerCount">层数。</param>
/// <remarks>
/// <para><b>不是逐层历史</b>：<c>docker history</c> 那样的逐层记录（每层的命令与大小）有专门的端点
/// 与逐层展开的界面，挤进详情响应只会让「打开详情」变慢。其中的层数在这里给出，
/// 让界面能直接显示「7 层」而不必再发一次请求。</para>
/// <para><b><see cref="ExposedPorts"/> / <see cref="Volumes"/> 是列表而不是字典</b>：
/// Docker 那两个字段的「值」是空的（它只是声明「这个端口 / 路径存在」），原样透传会得到
/// /// <c>{"80/tcp": {}}</c> 这种没有信息量的空对象。去重后的键列表表达得更准，
/// 也省掉前端一层 <c>Object.keys()</c>。</para>
/// <para><b><see cref="Env"/> 与标签同样可能含敏感值</b>（有人把构建参数写进 <c>ENV</c> 或
/// <c>LABEL</c>）。因此本投影只在 inspect 里给出，列表项都不带；0.3.0 接入逐资源授权后，
/// 非管理员将不再能拿到镜像级详情。</para>
/// </remarks>
public sealed record ImageDetail(
    string Id,
    IReadOnlyList<string> RepoTags,
    IReadOnlyList<string> RepoDigests,
    DateTimeOffset Created,
    long Size,
    string Architecture,
    string Os,
    string OsVersion,
    string Author,
    string Comment,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> ExposedPorts,
    IReadOnlyList<string> Volumes,
    IReadOnlyDictionary<string, string> Labels,
    string WorkingDir,
    int LayerCount);

/// <summary>
/// 拉取镜像的结果。
/// </summary>
/// <param name="ImageId">拉取后的镜像完整 ID。</param>
/// <param name="Reference">完整的镜像引用（<c>仓库:标签</c>）。</param>
/// <param name="Size">镜像大小（字节）。</param>
/// <param name="RepoTags">拉取后镜像上的全部标签。</param>
/// <remarks>
/// <b>为什么带大小而不是只给 ID</b>：拉取进度推给用户时，真正想说的是「拉了多少 MB」，
/// 而拿大小必须再 inspect 一次；既然这次 inspect 一定要做（确认确实落盘了），
/// 就把结果一起回带，省掉调用方紧随其后的第二个请求。
/// </remarks>
public sealed record ImagePullResult(
    string ImageId,
    string Reference,
    long Size,
    IReadOnlyList<string> RepoTags);

/// <summary>
/// 删除镜像的结果。
/// </summary>
/// <param name="Untagged">被解绑的标签（同一镜像 ID 的其他 <c>repoTag</c>）。</param>
/// <param name="Deleted">被删除的镜像 ID。</param>
/// <remarks>
/// <b>为什么必须回带</b>：一个镜像 ID 往往挂着多个标签，删掉其中一个时镜像本身还在。
/// 不告诉调用方哪些标签被解绑，界面上会出现「我明明删了，列表里另一个标签还在」的困惑。
/// 两者都是空数组是合法结果（只解绑引用、未删掉镜像本体）。
/// </remarks>
public sealed record ImageRemovedResult(IReadOnlyList<string> Untagged, IReadOnlyList<string> Deleted);
