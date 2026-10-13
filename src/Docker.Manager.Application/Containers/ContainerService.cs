using System.Globalization;
using System.Text;
using Docker.DotNet.Models;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Application.Docker;
using Microsoft.Extensions.Logging;
using SdkState = Docker.DotNet.Models.State;

namespace Docker.Manager.Application.Containers;

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
public sealed class ContainerService
{
    /// <summary>列表默认每页条数。</summary>
    private const int DefaultPageSize = 20;

    /// <summary>列表每页条数上限。</summary>
    /// <remarks>上限用于挡住「pageSize=100000」这种把整台宿主机容器一次吐出去的要求，避免响应体失控。</remarks>
    private const int MaxPageSize = 100;

    /// <summary><c>tail</c> 上限。</summary>
    /// <remarks>
    /// 日志是一次性整体返回的，<c>tail</c> 多大响应就多大。这里<b>报错</b>而不是悄悄夹紧：
    /// 调用方要 100 万行却只拿到 1 万行，会以为「日志就这么多」——错误比静默截断诚实。
    /// </remarks>
    private const int MaxLogTail = 10000;

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
        var all = request.All ?? false;

        var containers = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ListContainersAsync(new ContainersListParameters { All = all }, cancellationToken),
            "容器列表",
            cancellationToken).ConfigureAwait(false);

        var pageIndex = Math.Max(request.PageIndex ?? 0, 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        _logger.LogDebug(
            "主机 {HostId} 容器列表：共 {Total} 个（含已停止={All}），返回第 {PageIndex} 页（每页 {PageSize}）",
            hostId,
            containers.Count,
            all,
            pageIndex,
            pageSize);

        return new ContainerListResult(
            [.. containers.Skip(pageIndex * pageSize).Take(pageSize).Select(MapSummary)],
            containers.Count,
            pageIndex,
            pageSize);
    }

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
    /// 读取容器日志。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">日志请求（容器标识、行数、起始时间等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>日志文本。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空或 <c>tail</c> 越界（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么不支持 follow</b>：一次性 HTTP 响应跟着日志流没有意义（连接会一直挂到容器退出）。
    /// 实时日志是 0.3.0 的 SignalR 推送要解决的事。
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
            Timestamps = request.Timestamps ?? false,
            Tail = tail?.ToString(CultureInfo.InvariantCulture),
            Since = request.Since?.ToString(CultureInfo.InvariantCulture),
        };

        var collector = new LogCollector();

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.GetContainerLogsAsync(containerId, parameters, collector, cancellationToken),
            $"容器 {containerId} 的日志",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "读取主机 {HostId} 上容器 {ContainerId} 的日志：{Length} 字符（tail={Tail}，since={Since}）",
            hostId,
            containerId,
            collector.Length,
            parameters.Tail ?? "全部",
            parameters.Since ?? "不限");

        return new ContainerLogsResult(containerId, collector.ToString());
    }

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
        State: MapState(source.State),
        Config: MapConfig(source.Config),
        HostConfig: MapHostConfig(source.HostConfig),
        Mounts: source.Mounts is null ? [] : [.. source.Mounts.Select(MapMount)],
        Networks: MapNetworks(source.NetworkSettings?.Networks));

    /// <summary>
    /// 投影运行状态。
    /// </summary>
    /// <param name="source">SDK 状态。</param>
    /// <returns>状态投影。</returns>
    private static ContainerState MapState(SdkState? source) => new(
        Status: source?.Status ?? string.Empty,
        Running: source?.Running ?? false,
        Paused: source?.Paused ?? false,
        Restarting: source?.Restarting ?? false,
        StartedAt: ParseTimestamp(source?.StartedAt),
        FinishedAt: ParseTimestamp(source?.FinishedAt),
        // 运行中的容器 ExitCode 恒为 0，给出去会被读成「已正常退出」，因此运行中一律返回 null。
        ExitCode: source is null || source.Running ? null : source.ExitCode,
        OomKilled: source?.OOMKilled ?? false,
        HealthStatus: MapHealthStatus(source?.Health?.Status));

    /// <summary>
    /// 投影健康检查状态。
    /// </summary>
    /// <param name="status">SDK 的健康状态文本。</param>
    /// <returns>健康状态；无健康检查时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 容器没配健康检查时 Docker 给的字符串是 <c>none</c>。把它透传出去，前端要么写死一个魔法字符串判断，
    /// 要么把它渲染成一个名叫「none」的状态——都不如统一成 <see langword="null"/>：「没有这项信息」。
    /// </remarks>
    private static string? MapHealthStatus(string? status) =>
        string.IsNullOrEmpty(status) || string.Equals(status, "none", StringComparison.OrdinalIgnoreCase)
            ? null
            : status;

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
    /// 同步收集日志文本的 <see cref="IProgress{T}"/> 实现。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不用 <c>System.Progress&lt;string&gt;</c></b>：它把回调 <c>Post</c> 到线程池，
    /// 于是 <c>await GetContainerLogsAsync(...)</c> 返回时队列里可能还有没执行完的回调——
    /// 日志会偶尔缺尾巴，且缺在哪一次请求完全随机。同步实现没有这个时间窗。</para>
    /// <para><b>为什么可以不上锁</b>：SDK 在单条读取循环里顺序回调，不存在并发进入。
    /// 这是依赖库行为的前提，因此写在这里而不是靠猜。</para>
    /// </remarks>
    private sealed class LogCollector : IProgress<string>
    {
        private readonly StringBuilder _builder = new();

        /// <summary>已收集的字符数（用长度代替把内容整体取出来做统计，避免多一次大字符串分配）。</summary>
        public int Length => _builder.Length;

        /// <inheritdoc />
        public void Report(string value) => _builder.Append(value);

        /// <inheritdoc />
        public override string ToString() => _builder.ToString();
    }
}
