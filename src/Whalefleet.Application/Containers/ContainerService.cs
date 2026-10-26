using System.Globalization;
using System.Net;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;
using SdkState = Docker.DotNet.Models.State;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器查询用例（列表 / 详情 / 日志），对应 <c>/api/v1/hosts/{hostId}/containers/*</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么全部是「投影」</b>：SDK 的 <c>ContainerListResponse</c> / <c>ContainerInspectResponse</c>
/// 是对守护进程 JSON 的直译，字段随 Docker 版本增删。直接透传的后果是：前端要么跟着 SDK 升级改代码，
/// 要么在响应里找不到自己依赖的字段。这里的每个投影都在 <c>ContainerSummaries.cs</c> 里写明了语义，
/// 因此「响应里有什么」由本项目决定，不由 Docker 版本决定。</para>
/// <para><b>主机校验不在这里</b>：主机是否存在（<c>1300</c>）与当前用户能否看见它（<c>1501</c>）由
/// Api 层的 <c>CurrentHostMiddleware</c> 在进入本服务前完成；本服务只负责「拿客户端 + 调 Docker + 翻译异常」。</para>
/// <para><b>外部响应按可空处理</b>：SDK 模型把很多字段声明为非空，但它们的值来自另一个进程的 JSON。
/// 缺字段时抛 <see cref="NullReferenceException"/> 会被兜底成 <c>9999</c>/500，而这明明是「对端数据不全」，
/// 因此所有映射都按可空写，缺失就退化成空值而不是崩掉。</para>
/// <para><b>日志纪律</b>：只记主机标识、容器标识与条数，<b>不</b>记日志内容与环境变量
/// （命令输出里常有密码、Token——记进来等于把凭据写进日志文件）。</para>
/// </remarks>
public sealed partial class ContainerService
{
    /// <summary>列表默认每页条数。</summary>
    private const int DefaultPageSize = 20;

    /// <summary>列表每页条数上限。</summary>
    /// <remarks>上限用于挡住「pageSize=100000」这种把整台宿主机容器一次吐出去的要求，避免响应体失控。</remarks>
    private const int MaxPageSize = 100;

    /// <summary><c>tail</c> 上限。</summary>
    /// <remarks>
    /// <para>日志是一次性整体返回的，<c>tail</c> 多大响应就多大。这里<b>报错</b>而不是悄悄夹紧：
    /// 调用方要 100 万行却只拿到 1 万行，会以为「日志就这么多」——错误比静默截断诚实。</para>
    /// <para><b>0.5.0 从 10000 收紧到 5000</b>：日志改为结构化行数组后，
    /// 单行还要带时间戳与序号，响应体比纯文本大了一截。
    /// 而一次首屏加载 5000 行已经远超「看清问题」的需要——
    /// 真要更多，前端可以用 <c>until</c> 逐页往前翻，下载端点则完全不受此上限约束。</para>
    /// </remarks>
    private const int MaxLogTail = 5000;

    /// <summary><c>waitSeconds</c> 上限。</summary>
    private const int MaxWaitSeconds = 300;

    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<ContainerService> _logger;

    /// <summary>
    /// 初始化容器查询用例。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerService(IDockerHostAccessor accessor, ILogger<ContainerService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// 列出主机上的容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（是否含已停止、分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页数据与总数。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）、已停用（<c>1302</c>）、Docker 调用失败（<c>1402</c>）或超时（<c>1403</c>）。
    /// </exception>
    /// <remarks>
    /// 默认只列运行中的容器，与 <c>docker ps</c> 一致；<c>all=true</c> 才等价于 <c>docker ps -a</c>。
    /// </remarks>
    public async Task<ContainerListResult> ListAsync(
        string hostId,
        ListContainersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);
        var filter = ResolveStateFilter(request);

        var containers = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ListContainersAsync(
                new ContainersListParameters { All = filter.IncludeStopped },
                cancellationToken),
            "容器列表",
            cancellationToken).ConfigureAwait(false);

        // 过滤 → 排序 → 分页，顺序不能换：
        // 先分页再过滤会让 total 与实际命中数不一致（页脚说 100 个、翻过去只有 3 个）。
        var filtered = Filter(containers, request.Search, filter);

        // 客户端**显式**指定排序字段时才重排：那时顺序是用户要的；没指定就保持守护进程
        // 返回的顺序（创建时间降序），它本来就是稳定的，额外重排只会让「翻页时看到的东西变了」
        // 更难解释。排序必须在分页之前，否则 total 不变而每页内容错位。
        var ordered = Sort(filtered, request.SortBy, request.SortDir);

        var pageIndex = Math.Max(request.PageIndex ?? 0, 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        _logger.LogDebug(
            "主机 {HostId} 容器列表：共 {Total} 个（状态={State}，搜索={Search}，排序={SortBy}/{SortDir}），返回第 {PageIndex} 页（每页 {PageSize}）",
            hostId,
            ordered.Count,
            filter.Kind,
            string.IsNullOrWhiteSpace(request.Search) ? "无" : request.Search.Trim(),
            request.SortBy ?? "默认",
            request.SortDir ?? "desc",
            pageIndex,
            pageSize);

        return new ContainerListResult(
            [.. ordered.Skip(pageIndex * pageSize).Take(pageSize).Select(MapSummary)],
            ordered.Count,
            pageIndex,
            pageSize);
    }

    /// <summary>
    /// 状态筛选的解析结果。
    /// </summary>
    /// <param name="Kind">筛选种类（用于日志与调试）。</param>
    /// <param name="IncludeStopped">是否向守护进程请求「含已停止」的列表。</param>
    /// <param name="OnlyRunning">是否只保留运行类容器（<c>true</c> 时剔除未运行的）。</param>
    /// <param name="OnlyStopped">是否只保留未运行类容器（<c>true</c> 时剔除运行类的）。</param>
    private sealed record StateFilter(
        string Kind,
        bool IncludeStopped,
        bool OnlyRunning,
        bool OnlyStopped)
    {
        /// <summary>全部容器。</summary>
        public static StateFilter All { get; } = new(ContainerStateFilter.All, true, false, false);

        /// <summary>仅运行中。</summary>
        public static StateFilter Running { get; } = new(ContainerStateFilter.Running, false, true, false);

        /// <summary>仅未运行。</summary>
        public static StateFilter Stopped { get; } = new(ContainerStateFilter.Stopped, true, false, true);

        /// <summary>
        /// 判断某个状态是否命中本筛选。
        /// </summary>
        /// <param name="state">Docker 原始状态文本。</param>
        /// <returns>命中返回 <see langword="true"/>。</returns>
        public bool Matches(string? state)
        {
            if (OnlyRunning) return IsRunningState(state);
            if (OnlyStopped) return !IsRunningState(state);

            return true;
        }
    }

    /// <summary>
    /// 解析请求里的状态筛选。
    /// </summary>
    /// <param name="request">列表请求。</param>
    /// <returns>筛选解析结果。</returns>
    /// <remarks>
    /// <b>优先级：<c>state</c> &gt; <c>all</c></b>。<c>state</c> 是新契约，
    /// <c>all</c> 是旧契约（布尔开关表达不了「仅未运行」）。旧客户端不发 <c>state</c>，
    /// 因此按 <c>all</c> 的旧语义处理：<c>true</c> = 全部，<c>false</c> / 省略 = 仅运行中。
    /// <br/>
    /// <b>未知值按「全部」处理</b>，与排序字段同策略：筛选值来自客户端，
    /// 版本错配时不该让整个列表 400。
    /// </remarks>
    private static StateFilter ResolveStateFilter(ListContainersRequest request)
    {
        var state = request.State?.Trim();

        if (string.IsNullOrEmpty(state))
        {
            return request.All == true ? StateFilter.All : StateFilter.Running;
        }

        return state.ToLowerInvariant() switch
        {
            ContainerStateFilter.Running => StateFilter.Running,
            ContainerStateFilter.Stopped => StateFilter.Stopped,
            _ => StateFilter.All
        };
    }

    /// <summary>
    /// 判断状态是否属于「运行类」。
    /// </summary>
    /// <param name="state">Docker 原始状态文本。</param>
    /// <returns>运行中 / 已暂停 / 重启中返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>已暂停与重启中算「运行类」</b>：它们都还占着资源、还连着端口，
    /// 对「这个服务还活着吗」这个问题而言答案是「活着」。
    /// 守护进程自己判定「运行中」时用的也是同一套口径（<c>docker ps</c> 会列出它们）。
    /// </remarks>
    private static bool IsRunningState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "running" or "paused" or "restarting" => true,
        _ => false
    };

    /// <summary>
    /// 按状态与关键字过滤容器。
    /// </summary>
    /// <param name="containers">守护进程返回的容器。</param>
    /// <param name="search">搜索关键字；为空时不做关键字过滤。</param>
    /// <param name="filter">状态筛选。</param>
    /// <returns>命中的容器。</returns>
    /// <remarks>
    /// <b>必须在分页之前</b>：分页也在服务端内存里做，先分页再过滤会让
    /// <c>total</c> 与实际命中数对不上（页脚说 100 个，翻过去却只有 3 个）。
    /// </remarks>
    private static IReadOnlyList<ContainerListResponse> Filter(
        IList<ContainerListResponse> containers,
        string? search,
        StateFilter filter)
    {
        IEnumerable<ContainerListResponse> query = containers.Where(c => filter.Matches(c.State));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();

            query = query.Where(container =>
                Contains(MapName(container.Names, container.ID ?? string.Empty), keyword)
                || Contains(container.Image, keyword)
                || Contains(container.ID, keyword));
        }

        return [.. query];
    }

    /// <summary>
    /// 关键字包含判断（不区分大小写）。
    /// </summary>
    /// <param name="haystack">被检查的文本。</param>
    /// <param name="needle">关键字。</param>
    /// <returns>命中返回 <see langword="true"/>。</returns>
    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 按请求指定的字段排序。
    /// </summary>
    /// <param name="containers">守护进程返回的容器。</param>
    /// <param name="sortBy">排序字段（见 <see cref="ContainerSortField"/>）；为空表示不排序。</param>
    /// <param name="sortDir">排序方向；<c>asc</c> 为升序，其余按降序。</param>
    /// <returns>排序后的容器；未指定字段时原样返回。</returns>
    /// <remarks>
    /// <b>同值项用容器 ID 兜底排序</b>：<see cref="List{T}.Sort(Comparison{T})"/> 是不稳定排序，
    /// 而 <c>docker compose up</c> 一次拉起的一批容器创建时间戳常常完全相同。
    /// 没有次级键时，同两个容器在两次翻页之间可能换位，用户会以为有容器凭空消失了。
    /// <br/>
    /// <b>字符串用序数比较</b>：受当前区域设置影响的大小写排序会随系统语言变化，
    /// 同一个列表在不同机器上顺序不同，无法复现。
    /// </remarks>
    private static IReadOnlyList<ContainerListResponse> Sort(
        IReadOnlyList<ContainerListResponse> containers,
        string? sortBy,
        string? sortDir)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return [.. containers];
        }

        var field = sortBy.Trim();
        var descending = !string.Equals(sortDir?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);

        Comparison<ContainerListResponse> comparison = field switch
        {
            ContainerSortField.Name => (x, y) =>
                string.CompareOrdinal(NameKey(x), NameKey(y)),
            ContainerSortField.Image => (x, y) =>
                string.CompareOrdinal(ImageKey(x), ImageKey(y)),
            ContainerSortField.State => (x, y) =>
                string.CompareOrdinal(x.State ?? string.Empty, y.State ?? string.Empty),
            // 默认分支（含未知的未来字段）：退回创建时间，而不是拒绝请求
            _ => (x, y) => x.Created.CompareTo(y.Created),
        };

        var sorted = containers
            .Select((container, index) => (Container: container, Index: index))
            .ToList();

        sorted.Sort((x, y) =>
        {
            var result = descending ? comparison(y.Container, x.Container) : comparison(x.Container, y.Container);

            // 同值时用 ID 兜底，保证顺序稳定；ID 也相同（理论上不会）时回退到原下标
            if (result != 0)
            {
                var byId = string.CompareOrdinal(x.Container.ID ?? string.Empty, y.Container.ID ?? string.Empty);

                return byId != 0 ? byId : x.Index.CompareTo(y.Index);
            }

            return x.Index.CompareTo(y.Index);
        });

        return [.. sorted.Select(item => item.Container)];
    }

    /// <summary>
    /// 容器名排序键。
    /// </summary>
    /// <remarks>
    /// 复用 <see cref="MapName"/>：它已经把「去前导斜杠 / 无名退化为短 ID」处理好了。
    /// 排序键必须与表格里显示的字符串一致——否则会出现「按名称升序，但看起来没排」
    /// （因为用户看到的名字是去掉斜杠后的，键却带着斜杠），而这很难自查。
    /// </remarks>
    private static string NameKey(ContainerListResponse container) =>
        MapName(container.Names, container.ID ?? string.Empty);

    /// <summary>镜像名排序键：缺失时依次回退到镜像 ID、容器 ID。</summary>
    private static string ImageKey(ContainerListResponse container) =>
        container.Image ?? container.ImageID ?? container.ID ?? string.Empty;

    /// <summary>
    /// 查询容器详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（容器标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>容器详情投影。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空（<c>1000</c>）、容器不存在（<c>1401</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<ContainerDetail> InspectAsync(
        string hostId,
        InspectContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var container = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("主机 {HostId} 查询容器 {ContainerId} 详情", hostId, containerId);

        return MapDetail(container);
    }

    /// <summary>
    /// 读取容器日志（一次性返回，结构化行数组）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">日志请求（容器标识、行数、起止时间等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>日志行（序号从 1 起）。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空或 <c>tail</c> 越界（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么仍然不支持 follow</b>：一次性 HTTP 响应跟着日志流没有意义
    /// （连接会一直挂到容器退出）。实时日志走 0.5.0 的 SignalR 推送
    /// （<c>ContainerLogHub</c>）。</para>
    /// <para><b>为什么恒定请求时间戳</b>：客户端要按时间排序、要定位到具体时刻，
    /// 而 Docker 只在 <c>timestamps=1</c> 时才给行首前缀。
    /// 「页面上要不要显示时间戳」是<b>展示</b>决策，不该由<b>取数</b>决定——
    /// 否则同一个接口在两个页面下会给出不同形状的数据。</para>
    /// </remarks>
    public async Task<ContainerLogsResult> GetLogsAsync(
        string hostId,
        ContainerLogsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var tail = RequireTail(request.Tail);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ContainerLogsParameters
        {
            // stdout 与 stderr 都是用户要看的输出，默认全给；只要 stderr 是排查场景里的例外需求。
            ShowStdout = true,
            ShowStderr = request.IncludeStderr ?? true,
            Timestamps = true,
            Tail = tail?.ToString(CultureInfo.InvariantCulture),
            Since = request.Since?.ToString(CultureInfo.InvariantCulture),
            Until = request.Until?.ToString(CultureInfo.InvariantCulture),
        };

        var collector = new LogCollector();
        var parser = new ContainerLogParser(stripTimestampPrefix: true);
        var lines = new List<Abstractions.Realtime.ContainerLogLine>();
        var seq = 1;

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.GetContainerLogsAsync(containerId, parameters, collector, cancellationToken),
            $"容器 {containerId} 的日志",
            cancellationToken).ConfigureAwait(false);

        // 逐行回调 → 逐行产出。SDK 每次给的是一整行（不含换行符），
        // 详见 ContainerLogParser 的说明——直接拼接会得到「所有日志挤成一行」。
        foreach (var chunk in collector.Chunks)
        {
            foreach (var parsed in parser.Append(chunk))
            {
                lines.Add(new Abstractions.Realtime.ContainerLogLine(seq++, parsed.Timestamp, parsed.Content));
            }
        }

        _logger.LogInformation(
            "读取主机 {HostId} 上容器 {ContainerId} 的日志：{LineCount} 行（tail={Tail}，since={Since}，until={Until}）",
            hostId,
            containerId,
            lines.Count,
            parameters.Tail ?? "全部",
            parameters.Since ?? "不限",
            parameters.Until ?? "不限");

        // 「被截断」= 守护进程手上可能还有更早的日志没给我们。
        // 判据是「请求了 N 行且正好拿回 N 行」——拿回不足 N 行说明日志总共就这么多。
        // 这只是**可能**而非确定（容器恰好写了 N 行时也会为 true），
        // 但它足以让前端决定要不要显示「向上加载更多」：
        // 万一没有更多，向上加载一次拿到 0 行就知道到底了。
        var truncated = tail is { } requested && lines.Count >= requested;

        return new ContainerLogsResult(containerId, lines, truncated);
    }

    /// <summary>
    /// 创建容器（仅创建，<b>不</b>自动启动）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">创建请求（精选字段）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新容器的 ID 与守护进程提示。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）、镜像不存在（<c>1401</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么创建与启动分开</b>：两者失败原因完全不同（镜像 / 参数 vs 端口占用 / 依赖未就绪），
    /// 合成一步会让「容器建出来了但没起来」变成一个既不是成功也不是可重试失败的中间态——
    /// 分开后调用方看得见中间结果，也能在启动失败时重试而不重建容器。
    /// </remarks>
    public async Task<ContainerCreatedResult> CreateAsync(
        string hostId,
        CreateContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var parameters = BuildCreateParameters(request);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var created = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.CreateContainerAsync(parameters, cancellationToken),
            $"镜像 {parameters.Image}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 创建容器 {ContainerId}（镜像={Image}，名称={Name}）",
            hostId,
            created.ID,
            parameters.Image,
            parameters.Name ?? "（随机）");

        return new ContainerCreatedResult(created.ID, created.Warnings is null ? [] : [.. created.Warnings]);
    }

    /// <summary>
    /// 启动容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">启动请求（容器标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">容器标识为空（<c>1000</c>）、容器不存在（<c>1401</c>）或 Docker 调用失败（<c>1402</c>）。</exception>
    public async Task StartAsync(
        string hostId,
        StartContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.ExecuteStateChangeAsync(
            () => client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken),
            containerId,
            "启动",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 启动容器 {ContainerId}", hostId, containerId);
    }

    /// <summary>
    /// 停止容器（先发 SIGTERM，超时后 SIGKILL）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">停止请求（容器标识、等待秒数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">容器标识为空或 <c>waitSeconds</c> 越界（<c>1000</c>）、容器不存在（<c>1401</c>）或 Docker 调用失败（<c>1402</c>）。</exception>
    public async Task StopAsync(
        string hostId,
        StopContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var waitSeconds = RequireWaitSeconds(request.WaitSeconds);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        // 省略等待秒数时不传值：由守护进程用它自己的默认值（10 秒），
        // 在服务端写死同一个数字只会在 Docker 改默认值时静默跑偏。
        var parameters = new ContainerStopParameters { WaitBeforeKillSeconds = waitSeconds };

        await this.ExecuteStateChangeAsync(
            () => client.Containers.StopContainerAsync(containerId, parameters, cancellationToken),
            containerId,
            "停止",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 停止容器 {ContainerId}（等待={WaitSeconds}）",
            hostId,
            containerId,
            waitSeconds?.ToString(CultureInfo.InvariantCulture) ?? "默认");
    }

    /// <summary>
    /// 重启容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">重启请求（容器标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">容器标识为空（<c>1000</c>）、容器不存在（<c>1401</c>）或 Docker 调用失败（<c>1402</c>）。</exception>
    public async Task RestartAsync(
        string hostId,
        RestartContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.ExecuteStateChangeAsync(
            () => client.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters(), cancellationToken),
            containerId,
            "重启",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 重启容器 {ContainerId}", hostId, containerId);
    }

    /// <summary>
    /// 删除容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（容器标识、是否强制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 运行中且未指定 <c>force</c>（<c>1404</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>不删数据卷</b>：即使容器用了匿名卷，也不传 <c>RemoveVolumes</c>——删容器顺手删卷是
    /// 不可逆的数据丢失，而 API 里没有任何地方能让调用方看出「这次删除顺便带走了数据」。
    /// </remarks>
    public async Task RemoveAsync(
        string hostId,
        RemoveContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var force = request.Force ?? false;
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.RemoveAsync(client, containerId, force, hostId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量删除容器（逐条汇报失败，**部分成功也算成功**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">批量删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>汇总结果。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）或已停用（<c>1302</c>）；列表为空 / 超限时 <c>1000</c>。
    /// </exception>
    /// <remarks>
    /// <para><b>主机客户端只取一次</b>：<c>GetClientAsync</c> 会做可达性探测与凭据装配，
    /// N 个容器取 N 次就是 N 次探测。而且它抛的 <c>1301</c> / <c>1302</c> 是<b>整批</b>的失败
    /// （主机都连不上了，逐条汇报「100 个都失败了」对用户毫无帮助），因此它必须发生在循环之外。</para>
    /// <para><b>串行而不是并行</b>：删除容器会触发清理动作（网络断开、卷解绑），
    /// 并发删除时守护进程内部的锁会让失败原因变得不可预测；而单次删除通常在几十毫秒内完成，
    /// 省下的那点时间远不如「失败结果能解释」值钱。</para>
    /// </remarks>
    public async Task<BatchOperationResult> BatchRemoveAsync(
        string hostId,
        BatchRemoveContainersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerIds = BatchRequest.NormalizeIds(request.ContainerIds, nameof(request.ContainerIds));
        var force = request.Force ?? false;
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var failures = new List<BatchOperationFailure>();

        foreach (var containerId in containerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await this.RemoveAsync(client, containerId, force, hostId, cancellationToken).ConfigureAwait(false);
            }
            catch (BusinessException exception)
            {
                // 逐条收下：批量场景下「哪一条、为什么」比「整批失败」有用得多
                failures.Add(new BatchOperationFailure(containerId, exception.Code, exception.Message));
            }
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "主机 {HostId} 批量删除容器：成功 {Succeeded}/{Requested}，失败 {Failed} 条（强制={Force}）",
                hostId,
                containerIds.Count - failures.Count,
                containerIds.Count,
                failures.Count,
                force);
        }
        else
        {
            _logger.LogInformation(
                "主机 {HostId} 批量删除容器：全部 {Count} 个成功（强制={Force}）",
                hostId,
                containerIds.Count,
                force);
        }

        return new BatchOperationResult(containerIds.Count, containerIds.Count - failures.Count, failures);
    }

    /// <summary>
    /// 用已有客户端删除单个容器（单条与批量端点共用）。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="force">是否强制删除。</param>
    /// <param name="hostId">主机标识（仅用于日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task RemoveAsync(
        IDockerClient client,
        string containerId,
        bool force,
        string hostId,
        CancellationToken cancellationToken)
    {
        var parameters = new ContainerRemoveParameters { Force = force, RemoveVolumes = false };

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.RemoveContainerAsync(containerId, parameters, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 删除容器 {ContainerId}（强制={Force}）", hostId, containerId, force);
    }

    /// <summary>
    /// 执行「目标状态已达成就算成功」的容器状态变更。
    /// </summary>
    /// <param name="operation">Docker 调用。</param>
    /// <param name="containerId">容器标识（用于日志与错误消息）。</param>
    /// <param name="action">动作名（用于日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <para>启动已在运行的容器、停止已停止的容器时，守护进程返回 <c>304 Not Modified</c>。
    /// SDK 把它当失败抛出来，于是「重复点一下按钮」会变成一个 500——而这明明是成功的语义。</para>
    /// <para>类中只有这三个（启动 / 停止 / 重启）是幂等的；<b>删除</b>不适用：容器已经没了应当如实
    /// 报 <c>1401</c>，而不是假装删掉了什么。</para>
    /// </remarks>
    private async Task ExecuteStateChangeAsync(
        Func<Task> operation,
        string containerId,
        string action,
        CancellationToken cancellationToken)
    {
        try
        {
            await DockerErrorTranslator
                .ExecuteAsync(operation, $"容器 {containerId}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BusinessException exception) when (
            exception.InnerException is DockerApiException { StatusCode: HttpStatusCode.NotModified })
        {
            _logger.LogDebug(
                "容器 {ContainerId} 已处于「{Action}」的目标状态，按成功处理（守护进程返回 304）",
                containerId,
                action);
        }
    }

    /// <summary>
    /// 把创建请求映射成 SDK 参数，并完成全部本地校验。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <returns>可直接提交给守护进程的参数。</returns>
    /// <exception cref="BusinessException">任一字段非法（<c>1000</c>）。</exception>
    /// <remarks>
    /// 在本地尽量把能判的都判掉，而不是把请求原样丢给守护进程再看它报不报错：
    /// 本地校验能给出<b>指向具体字段</b>的中文消息，而守护进程的 400 只会说「invalid parameter」。
    /// </remarks>
    // SDK 把 Cmd / Env / Labels / WorkingDir / NetworkMode / PortBinding.HostIP 这类字段声明为**非空**，
    // 但它们实际都是可选的：SDK 序列化时会省略 null（与 Docker 的「未指定」语义一致），
    // 而传空数组 / 空串会被当成「显式清空」——例如把 Cmd 传成 [] 会让容器不带任何启动命令。
    // 因此这里必须传 null，只用 pragma 关掉与 SDK 标注的不一致。
#pragma warning disable CS8601
    private static CreateContainerParameters BuildCreateParameters(CreateContainerRequest request)
    {
        var (exposedPorts, portBindings) = BuildPorts(request.Ports);
        var (capAdd, capDrop) = AdvancedCreateOptions.BuildCapabilities(request.CapAdd, request.CapDrop);

        return new CreateContainerParameters
        {
            Image = RequireImage(request.Image),
            Name = RequireContainerName(request.Name),
            Cmd = request.Cmd is null ? null : [.. request.Cmd],
            Entrypoint = request.Entrypoint is null ? null : [.. request.Entrypoint],
            Env = RequireEnv(request.Env),
            Labels = request.Labels is null
                ? null
                : new Dictionary<string, string>(request.Labels, StringComparer.Ordinal),
            WorkingDir = request.WorkingDir,
            Hostname = request.Hostname,
            Tty = request.Tty ?? false,
            OpenStdin = request.StdinOpen ?? false,
            // 空串会被守护进程当成「显式指定空用户」，而不是「用镜像里的 USER」，
            // 因此这里把空串一并归一成 null。
            User = string.IsNullOrWhiteSpace(request.User) ? null : request.User.Trim(),
            ExposedPorts = exposedPorts,
            Healthcheck = AdvancedCreateOptions.BuildHealthCheck(request.HealthCheck),
            HostConfig = new HostConfig
            {
                AutoRemove = request.AutoRemove ?? false,
                NetworkMode = request.Network,
                Binds = RequireVolumes(request.Volumes),
                PortBindings = portBindings,
                RestartPolicy = BuildRestartPolicy(request.RestartPolicy),
                CapAdd = capAdd,
                CapDrop = capDrop,
                Devices = AdvancedCreateOptions.BuildDevices(request.Devices),
                Sysctls = AdvancedCreateOptions.BuildSysctls(request.Sysctls),
                Ulimits = AdvancedCreateOptions.BuildUlimits(request.Ulimits),
                LogConfig = AdvancedCreateOptions.BuildLogDriver(request.LogDriver),
                // 0 表示「不限制」，而把 0 显式传给守护进程与不传是等价的，
                // 因此这里直接写值（不限定为 null）——inspect 回读时统一投影成 null。
                Memory = AdvancedCreateOptions.RequireMemoryLimit(request.MemoryLimitBytes) ?? 0,
                NanoCPUs = AdvancedCreateOptions.RequireNanoCpus(request.NanoCpus) ?? 0,
            },
        };
    }

    /// <summary>
    /// 构建端口暴露与端口绑定两半参数。
    /// </summary>
    /// <param name="ports">请求里的端口映射。</param>
    /// <returns>暴露端口字典与绑定字典（无端口时为两个 <see langword="null"/>）。</returns>
    /// <exception cref="BusinessException">端口越界、协议非法或同一端口重复（<c>1000</c>）。</exception>
    /// <remarks>
    /// Docker 的创建接口把端口拆成两个字段（<c>ExposedPorts</c> 与 <c>HostConfig.PortBindings</c>），
    /// 键都是 <c>"容器端口/协议"</c>。对外只暴露一个有语义的 <c>containerPort</c>，
    /// 由这里负责把它翻译成两侧的键——两处键一旦拼得不一致，容器能建出来但端口不通。
    /// </remarks>
    private static (IDictionary<string, EmptyStruct>? Exposed, IDictionary<string, IList<PortBinding>>? Bindings)
        BuildPorts(IReadOnlyList<ContainerPortMapping>? ports)
    {
        if (ports is not { Count: > 0 })
        {
            return (null, null);
        }

        var exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);

        foreach (var port in ports)
        {
            RequirePortNumber(port.ContainerPort, "containerPort");

            var protocol = RequireProtocol(port.Type);
            var key = $"{port.ContainerPort.ToString(CultureInfo.InvariantCulture)}/{protocol}";

            if (!exposed.TryAdd(key, default))
            {
                throw new BusinessException(ErrorCodes.ValidationFailed, $"端口 {key} 在请求里出现了多次");
            }

            if (port.HostPort is not { } hostPort)
            {
                // 只暴露不绑定（等价于 Dockerfile 的 EXPOSE）：随机映射到宿主端口。
                continue;
            }

            RequirePortNumber(hostPort, "hostPort");

            bindings[key] =
            [
                new PortBinding
                {
                    HostPort = hostPort.ToString(CultureInfo.InvariantCulture),
                    // 空字符串会被守护进程当成「未指定」，显式转成 null 免得依赖它对空串的宽容。
                    HostIP = string.IsNullOrWhiteSpace(port.HostIp) ? null : port.HostIp.Trim(),
                },
            ];
        }

        return (exposed, bindings);
    }
#pragma warning restore CS8601

    /// <summary>
    /// 校验镜像名。
    /// </summary>
    /// <param name="image">请求里的镜像名。</param>
    /// <returns>去空白后的镜像名。</returns>
    /// <exception cref="BusinessException">为空时抛 <c>1000</c>。</exception>
    private static string RequireImage(string? image) =>
        string.IsNullOrWhiteSpace(image)
            ? throw new BusinessException(ErrorCodes.ValidationFailed, "image 不能为空")
            : image.Trim();

    /// <summary>
    /// 校验容器名。
    /// </summary>
    /// <param name="name">请求里的容器名。</param>
    /// <returns>去空白后的容器名；未指定时为 <see langword="null"/>（由守护进程随机命名）。</returns>
    /// <exception cref="BusinessException">字符集非法时抛 <c>1000</c>。</exception>
    /// <remarks>规则来自 <c>docker run --name</c>：以字母或数字开头，其后只允许字母、数字与 <c>_ . -</c>。</remarks>
    private static string? RequireContainerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var isValid = char.IsAsciiLetterOrDigit(trimmed[0])
            && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

        return isValid
            ? trimmed
            : throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"容器名「{name}」只能包含字母、数字与 _ . -，且必须以字母或数字开头");
    }

    /// <summary>
    /// 校验环境变量列表。
    /// </summary>
    /// <param name="env">请求里的环境变量。</param>
    /// <returns>原样透传的环境变量；未指定时为 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">不是 <c>KEY=VALUE</c> 形式或 KEY 为空时抛 <c>1000</c>。</exception>
    private static List<string>? RequireEnv(IReadOnlyList<string>? env)
    {
        if (env is not { Count: > 0 })
        {
            return null;
        }

        var values = new List<string>(env.Count);

        foreach (var entry in env)
        {
            if (entry?.IndexOf('=', StringComparison.Ordinal) is not > 0)
            {
                throw new BusinessException(
                    ErrorCodes.ValidationFailed,
                    $"环境变量「{entry}」必须是 KEY=VALUE 形式且 KEY 不能为空");
            }

            values.Add(entry);
        }

        return values;
    }

    /// <summary>
    /// 校验挂载列表并拼成 <c>Binds</c> 形式。
    /// </summary>
    /// <param name="volumes">请求里的挂载。</param>
    /// <returns><c>source:destination[:ro]</c> 列表；未指定时为 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">来源或目标为空时抛 <c>1000</c>。</exception>
    /// <remarks>
    /// 用 <c>Binds</c> 而非 <c>Mounts</c>：前者同时支持宿主路径与具名卷（由守护进程判定），
    /// 后者需要调用方先说清楚是哪种挂载（<c>bind</c> / <c>volume</c>）——对外少一个必填字段。
    /// 路径本身的合法性（是否存在、是否允许访问）交给守护进程判定。
    /// </remarks>
    private static List<string>? RequireVolumes(IReadOnlyList<ContainerVolumeMapping>? volumes)
    {
        if (volumes is not { Count: > 0 })
        {
            return null;
        }

        var binds = new List<string>(volumes.Count);

        foreach (var volume in volumes)
        {
            var source = RequireText(volume.Source, "volumes[].source");
            var destination = RequireText(volume.Destination, "volumes[].destination");

            binds.Add(volume.ReadOnly == true ? $"{source}:{destination}:ro" : $"{source}:{destination}");
        }

        return binds;
    }

    /// <summary>
    /// 校验重启策略并映射成 SDK 枚举。
    /// </summary>
    /// <param name="policy">请求里的重启策略（Docker 线上写法）。</param>
    /// <returns>SDK 的重启策略对象；未指定时为 <c>no</c>。</returns>
    /// <exception cref="BusinessException">取值非法时抛 <c>1000</c>。</exception>
    private static RestartPolicy BuildRestartPolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return new RestartPolicy { Name = RestartPolicyKind.No };
        }

        var kind = policy.Trim().ToLowerInvariant() switch
        {
            "no" => RestartPolicyKind.No,
            "always" => RestartPolicyKind.Always,
            "on-failure" => RestartPolicyKind.OnFailure,
            "unless-stopped" => RestartPolicyKind.UnlessStopped,
            _ => throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"restartPolicy「{policy}」非法，只能是 no / always / on-failure / unless-stopped"),
        };

        return new RestartPolicy { Name = kind };
    }

    /// <summary>
    /// 校验端口号范围。
    /// </summary>
    /// <param name="port">端口号。</param>
    /// <param name="field">出错消息里的字段名（线上写法）。</param>
    /// <exception cref="BusinessException">越界时抛 <c>1000</c>。</exception>
    private static void RequirePortNumber(int port, string field)
    {
        if (port is < 1 or > 65535)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"{field} 必须在 1 到 65535 之间（当前 {port.ToString(CultureInfo.InvariantCulture)}）");
        }
    }

    /// <summary>
    /// 校验并归一化端口协议。
    /// </summary>
    /// <param name="protocol">请求里的协议。</param>
    /// <returns><c>tcp</c> 或 <c>udp</c>。</returns>
    /// <exception cref="BusinessException">取值非法时抛 <c>1000</c>。</exception>
    private static string RequireProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return "tcp";
        }

        var normalized = protocol.Trim().ToLowerInvariant();

        return normalized is "tcp" or "udp"
            ? normalized
            : throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"协议「{protocol}」非法，只能是 tcp / udp");
    }

    /// <summary>
    /// 校验必填文本字段。
    /// </summary>
    /// <param name="value">字段值。</param>
    /// <param name="field">出错消息里的字段名（线上写法）。</param>
    /// <returns>去空白后的文本。</returns>
    /// <exception cref="BusinessException">为空时抛 <c>1000</c>。</exception>
    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new BusinessException(ErrorCodes.ValidationFailed, $"{field} 不能为空")
            : value.Trim();

    /// <summary>
    /// <c>waitSeconds</c> 范围校验。
    /// </summary>
    /// <param name="waitSeconds">请求里的等待秒数。</param>
    /// <returns>去空白后的秒数；未指定时为 <see langword="null"/>（用守护进程默认值）。</returns>
    /// <exception cref="BusinessException">越界时抛 <c>1000</c>。</exception>
    /// <remarks>上限 300 秒：再长的优雅停止已经与「卡死」无法区分，与其让请求挂在那里不如直接报错。</remarks>
    private static uint? RequireWaitSeconds(int? waitSeconds) => waitSeconds switch
    {
        null => null,
        < 0 or > MaxWaitSeconds => throw new BusinessException(
            ErrorCodes.ValidationFailed,
            $"waitSeconds 必须在 0 到 {MaxWaitSeconds} 之间（省略表示用 Docker 默认值）"),
        _ => (uint)waitSeconds.Value,
    };

    /// <summary>
    /// 容器标识必填校验。
    /// </summary>
    /// <param name="containerId">请求里的容器标识。</param>
    /// <returns>去空白后的容器标识。</returns>
    /// <exception cref="BusinessException">为空时抛 <c>1000</c>。</exception>
    /// <remarks>
    /// <b>为什么不用 <c>[Required]</c> 特性 + 模型校验</b>：那样错误消息是框架生成的英文占位文本，
    /// 而本项目对外统一用中文可读消息（错误码 <c>1000</c> 的 message 会直接展示给用户）。
    /// </remarks>
    private static string RequireContainerId(string? containerId) =>
        string.IsNullOrWhiteSpace(containerId)
            ? throw new BusinessException(ErrorCodes.ValidationFailed, "containerId 不能为空")
            : containerId.Trim();

    /// <summary>
    /// <c>tail</c> 范围校验。
    /// </summary>
    /// <param name="tail">请求里的行数。</param>
    /// <returns>去掉空白后的行数文本；未指定时为 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">越界时抛 <c>1000</c>。</exception>
    private static int? RequireTail(int? tail) => tail switch
    {
        null => null,
        < 0 or > MaxLogTail => throw new BusinessException(
            ErrorCodes.ValidationFailed,
            $"tail 必须在 0 到 {MaxLogTail} 之间（省略表示全部）"),
        _ => tail,
    };

    /// <summary>
    /// 把列表项投影成对外结构。
    /// </summary>
    /// <param name="source">SDK 列表项。</param>
    /// <returns>列表项投影。</returns>
    private static ContainerSummary MapSummary(ContainerListResponse source)
    {
        var ports = source.Ports is null ? [] : source.Ports.Select(MapPort).ToArray();

        return new ContainerSummary(
            Id: source.ID,
            Name: MapName(source.Names, source.ID),
            Image: source.Image,
            ImageId: source.ImageID,
            Command: source.Command,
            Created: ToUtc(source.Created),
            State: source.State,
            Status: source.Status,
            Ports: ports);
    }

    /// <summary>
    /// 取容器名。
    /// </summary>
    /// <param name="names">SDK 返回的名字列表（首个名字带前导斜杠，如 <c>/nginx</c>）。</param>
    /// <param name="id">容器 ID（无名字时用来兜底）。</param>
    /// <returns>不带前导斜杠的名字；一个名字都没有时退化为 12 位短 ID。</returns>
    /// <remarks>
    /// 前导斜杠来自 Docker 内部路径式命名（<c>/nginx</c>），对用户没有意义，所有展示位置都不该带上。
    /// </remarks>
    private static string MapName(IList<string>? names, string id)
    {
        var name = names?.FirstOrDefault()?.TrimStart('/');

        return string.IsNullOrEmpty(name) ? ShortId(id) : name;
    }

    /// <summary>
    /// 取 12 位短 ID。
    /// </summary>
    /// <param name="id">容器或镜像 ID。</param>
    /// <returns>前 12 个字符；不足 12 位时原样返回。</returns>
    private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>
    /// 把端口映射投影成对外结构。
    /// </summary>
    /// <param name="source">SDK 端口项。</param>
    /// <returns>端口投影。</returns>
    private static ContainerPort MapPort(PortSummary source) => new(
        PrivatePort: source.PrivatePort,
        PublicPort: source.PublicPort,
        Type: source.Type,
        Ip: source.IP);

    /// <summary>
    /// 把 inspect 结果投影成对外结构。
    /// </summary>
    /// <param name="source">SDK 详情。</param>
    /// <returns>详情投影。</returns>
    private static ContainerDetail MapDetail(ContainerInspectResponse source) => new(
        Id: source.ID,
        Name: source.Name?.TrimStart('/') ?? string.Empty,
        Created: ToUtc(source.Created),
        // inspect 顶层的 Image 是**镜像 ID**（sha256:…），镜像名在 Config.Image 里。
        // 对外契约里的 Image 指镜像名，故取 Config.Image。
        Image: source.Config?.Image ?? source.Image,
        State: MapState(source.State, source.RestartCount),
        Config: MapConfig(source.Config),
        HostConfig: MapHostConfig(source.HostConfig),
        Mounts: source.Mounts is null ? [] : [.. source.Mounts.Select(MapMount)],
        Networks: MapNetworks(source.NetworkSettings?.Networks));

    /// <summary>
    /// 投影运行状态。
    /// </summary>
    /// <param name="source">SDK 状态。</param>
    /// <param name="restartCount">
    /// 累计重启次数。<b>取自 inspect 的根对象</b>——它不在 <c>State</c> 里，
    /// 因此必须由调用方传入，而不是在这里读 <c>source</c>。
    /// </param>
    /// <returns>状态投影。</returns>
    private static ContainerState MapState(SdkState? source, long restartCount) => new(
        Status: source?.Status ?? string.Empty,
        Running: source?.Running ?? false,
        Paused: source?.Paused ?? false,
        Restarting: source?.Restarting ?? false,
        StartedAt: ParseTimestamp(source?.StartedAt),
        FinishedAt: ParseTimestamp(source?.FinishedAt),
        // 运行中的容器 ExitCode 恒为 0，给出去会被读成「已正常退出」，因此运行中一律返回 null。
        ExitCode: source is null || source.Running ? null : source.ExitCode,
        OomKilled: source?.OOMKilled ?? false,
        RestartCount: restartCount,
        // 未配健康检查时 Docker 不返回 Health 对象，统一归到 None（不是 null）——
        // 「没配」与「查不到」是两件事，混成一个值就没法在界面上分开表达
        HealthStatus: ContainerHealth.FromInspect(source?.Health?.Status));

    /// <summary>
    /// 投影启动配置。
    /// </summary>
    /// <param name="source">SDK 配置。</param>
    /// <returns>启动配置投影。</returns>
    private static ContainerConfigDetail MapConfig(ContainerConfig? source) => new(
        Image: source?.Image ?? string.Empty,
        Cmd: source?.Cmd is null ? [] : [.. source.Cmd],
        Entrypoint: source?.Entrypoint is null ? [] : [.. source.Entrypoint],
        Env: source?.Env is null ? [] : [.. source.Env],
        Labels: source?.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source.Labels, StringComparer.Ordinal),
        WorkingDir: source?.WorkingDir ?? string.Empty,
        Hostname: source?.Hostname ?? string.Empty,
        User: source?.User ?? string.Empty,
        Tty: source?.Tty ?? false);

    /// <summary>
    /// 投影宿主侧配置。
    /// </summary>
    /// <param name="source">SDK 宿主配置。</param>
    /// <returns>宿主侧配置投影。</returns>
    /// <remarks>
    /// <c>Memory</c> / <c>NanoCPUs</c> 为 0 表示**未限制**，对外统一成 <see langword="null"/>：
    /// 0 字节内存上限是没法运行的状态，把它当成「限制为 0」会让前端显示成一台跑不起来的容器。
    /// </remarks>
    private static ContainerHostConfigDetail MapHostConfig(HostConfig? source) => new(
        RestartPolicy: MapRestartPolicy(source?.RestartPolicy?.Name),
        AutoRemove: source?.AutoRemove ?? false,
        NetworkMode: source?.NetworkMode ?? string.Empty,
        Privileged: source?.Privileged ?? false,
        MemoryLimitBytes: source is null || source.Memory == 0 ? null : source.Memory,
        NanoCpus: source is null || source.NanoCPUs == 0 ? null : source.NanoCPUs);

    /// <summary>
    /// 把重启策略枚举还原成 Docker 的线上写法。
    /// </summary>
    /// <param name="kind">SDK 枚举值。</param>
    /// <returns>线上写法（<c>no</c> / <c>always</c> / <c>on-failure</c> / <c>unless-stopped</c>）。</returns>
    /// <remarks>
    /// 直接用 <c>ToString()</c> 会得到 <c>OnFailure</c> 这种 .NET 命名，而 <c>docker inspect</c> 与
    /// 前端表单里用的是 <c>on-failure</c>。对外契约跟随 Docker 线上写法，避免前端为每个枚举值做一次映射。
    /// </remarks>
    private static string MapRestartPolicy(RestartPolicyKind? kind) => kind switch
    {
        RestartPolicyKind.Always => "always",
        RestartPolicyKind.OnFailure => "on-failure",
        RestartPolicyKind.UnlessStopped => "unless-stopped",
        _ => "no",
    };

    /// <summary>
    /// 投影挂载。
    /// </summary>
    /// <param name="source">SDK 挂载项。</param>
    /// <returns>挂载投影。</returns>
    private static ContainerMountDetail MapMount(MountPoint source) => new(
        Type: source.Type ?? string.Empty,
        Source: source.Source ?? string.Empty,
        Destination: source.Destination ?? string.Empty,
        ReadWrite: source.RW);

    /// <summary>
    /// 投影网络接入信息。
    /// </summary>
    /// <param name="source">SDK 网络字典（键为网络名）。</param>
    /// <returns>网络投影列表；未接入任何网络时为空列表。</returns>
    private static IReadOnlyList<ContainerNetworkDetail> MapNetworks(IDictionary<string, EndpointSettings>? source) =>
        source is null
            ? []
            : [.. source.Select(pair => new ContainerNetworkDetail(
                Name: pair.Key,
                IpAddress: pair.Value?.IPAddress ?? string.Empty,
                Gateway: pair.Value?.Gateway ?? string.Empty,
                MacAddress: pair.Value?.MacAddress ?? string.Empty))];

    /// <summary>
    /// 把 Docker 时间戳解析成 <see cref="DateTimeOffset"/>。
    /// </summary>
    /// <param name="value">时间戳文本。</param>
    /// <returns>解析成功且是真实时间时返回该时刻；否则返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>Docker 用 <c>0001-01-01T00:00:00Z</c> 表示「未启动 / 未结束」。直接返回这个时刻，
    /// 前端会渲染出「0001 年 1 月 1 日」这种明显是 bug 的展示，因此按 <see langword="null"/> 处理。</para>
    /// <para><b>不能用 <c>DateTimeOffset.MinValue</c> 判断</b>：时区偏移参与比较，
    /// <c>MinValue</c> 在 UTC+8 下与 <c>0001-01-01T00:00:00Z</c> 并不相等。这里比较年份，与时区无关。</para>
    /// </remarks>
    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed) && parsed.Year > 1
            ? parsed
            : null;

    /// <summary>
    /// 把 SDK 的 <see cref="DateTime"/> 统一成 UTC 时刻。
    /// </summary>
    /// <param name="value">SDK 解析出的时间。</param>
    /// <returns>带时区的时刻。</returns>
    /// <remarks>
    /// Docker 的 JSON 里时间始终是 UTC，但 SDK 反序列化后 <see cref="DateTime.Kind"/> 可能是
    /// <see cref="DateTimeKind.Unspecified"/>（表现为「本地时间」）。按 UTC 解释它是唯一与协议一致的选择。
    /// </remarks>
    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => new DateTimeOffset(value),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    /// <summary>
    /// 同步收集日志块的 <see cref="IProgress{T}"/> 实现。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不用 <c>System.Progress&lt;string&gt;</c></b>：它把回调 <c>Post</c> 到线程池，
    /// 于是 <c>await GetContainerLogsAsync(...)</c> 返回时队列里可能还有没执行完的回调——
    /// 日志会偶尔缺尾巴，且缺在哪一次请求完全随机。同步实现没有这个时间窗。</para>
    /// <para><b>为什么可以不上锁</b>：SDK 在单条读取循环里顺序回调，不存在并发进入。
    /// 这是依赖库行为的前提，因此写在这里而不是靠猜。</para>
    /// <para><b>为什么逐块保存而不是拼成一个字符串</b>（0.5.0）：SDK 逐行回调且<b>不带换行符</b>，
    /// 拼起来会得到「所有日志挤成一行」。详见 <see cref="ContainerLogParser"/> 的说明。
    /// 保留分块才能让解析器把行切开。</para>
    /// </remarks>
    private sealed class LogCollector : IProgress<string>
    {
        private readonly List<string> _chunks = [];

        /// <summary>已收集的字符数（用长度代替把内容整体取出来做统计，避免多一次大字符串分配）。</summary>
        public int Length
        {
            get
            {
                var total = 0;

                foreach (var chunk in _chunks)
                {
                    total += chunk.Length;
                }

                return total;
            }
        }

        /// <summary>已收集的块（顺序与回调顺序一致）。</summary>
        public IReadOnlyList<string> Chunks => _chunks;

        /// <inheritdoc />
        public void Report(string value) => _chunks.Add(value);
    }
}
