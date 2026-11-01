namespace Whalefleet.Application.Stacks;

/// <summary>
/// 解析后的 Compose 服务定义。
/// </summary>
/// <remarks>
/// <b>字段是「解析后的确定形态」</b>，不是 YAML 的原样搬运：
/// 端口已经拆成结构化的三元组、命令已经拆成 argv、环境变量已经统一成
/// <c>KEY=VALUE</c> 列表。这样下游（规划器 / 执行器）不必再关心 YAML 的各种写法。
/// </remarks>
public sealed record ComposeService
{
    /// <summary>
    /// 服务名（YAML 里 <c>services</c> 下的键）。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 镜像名。
    /// </summary>
    /// <remarks>
    /// <b>必填</b>：本版本不支持 <c>build</c>，因此镜像只能显式给出。
    /// 服务没写镜像时不能「猜」——猜错的后果是部署出一个用户没要求的容器。
    /// </remarks>
    public required string Image { get; init; }

    /// <summary>
    /// 环境变量（<c>KEY=VALUE</c> 列表）。
    /// </summary>
    public IReadOnlyList<string> Environment { get; init; } = [];

    /// <summary>
    /// 启动命令（argv 形式；空列表表示不覆盖镜像的 CMD）。
    /// </summary>
    public IReadOnlyList<string> Command { get; init; } = [];

    /// <summary>
    /// 端口映射。
    /// </summary>
    public IReadOnlyList<ComposePortMapping> Ports { get; init; } = [];

    /// <summary>
    /// 挂载点。
    /// </summary>
    public IReadOnlyList<ComposeVolumeMount> Volumes { get; init; } = [];

    /// <summary>
    /// 接入的网络（顶层 <c>networks</c> 里的名字）。
    /// </summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    /// <summary>
    /// 依赖的服务（启动顺序的依据）。
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// 重启策略（<c>no</c> / <c>always</c> / <c>on-failure</c> / <c>unless-stopped</c>）；<see langword="null"/> 表示未指定。
    /// </summary>
    public string? Restart { get; init; }

    /// <summary>
    /// 健康检查定义；未配置时为 <see langword="null"/>。
    /// </summary>
    public ComposeHealthCheck? HealthCheck { get; init; }
}

/// <summary>
/// Compose 端口映射（短语法解析结果）。
/// </summary>
/// <param name="ContainerPort">容器内端口。</param>
/// <param name="HostPort">宿主端口；<see langword="null"/> 表示只暴露不绑定（随机映射）。</param>
/// <param name="Protocol">协议（<c>tcp</c> / <c>udp</c>）。</param>
/// <param name="HostIp">绑定的宿主地址；<see langword="null"/> 表示所有网卡。</param>
public sealed record ComposePortMapping(
    int ContainerPort,
    int? HostPort,
    string Protocol,
    string? HostIp);

/// <summary>
/// Compose 挂载点（短语法解析结果）。
/// </summary>
/// <param name="Source">宿主路径（bind）或卷名（具名卷）。</param>
/// <param name="Destination">容器内路径。</param>
/// <param name="ReadOnly">是否只读。</param>
/// <param name="IsBind"><see langword="true"/> 表示 <paramref name="Source"/> 是宿主路径，<see langword="false"/> 表示是具名卷。</param>
public sealed record ComposeVolumeMount(
    string Source,
    string Destination,
    bool ReadOnly,
    bool IsBind);

/// <summary>
/// Compose 健康检查定义。
/// </summary>
/// <remarks>
/// <b>时长一律以秒表示</b>（<see cref="ComposeDuration"/> 负责换算），
/// 而 Docker 侧的纳秒换算交给 <c>AdvancedCreateOptions</c>——两处各管一段，
/// 避免同一个「1.5 秒」在两处被各算一次。
/// </remarks>
/// <param name="Test">检查命令（Docker 的 <c>CMD</c> / <c>CMD-SHELL</c> / <c>NONE</c> 三态）。</param>
/// <param name="IntervalSeconds">检查间隔（秒）。</param>
/// <param name="TimeoutSeconds">单次检查超时（秒）。</param>
/// <param name="StartPeriodSeconds">启动宽限期（秒）。</param>
/// <param name="Retries">连续失败多少次判定为不健康。</param>
public sealed record ComposeHealthCheck(
    IReadOnlyList<string> Test,
    int? IntervalSeconds,
    int? TimeoutSeconds,
    int? StartPeriodSeconds,
    int? Retries);

/// <summary>
/// 解析后的顶层网络定义。
/// </summary>
/// <param name="Name">逻辑名（YAML 里的键）。</param>
/// <param name="ActualName">实际网络名；为 <see langword="null"/> 时用 <paramref name="Name"/>。</param>
/// <param name="External">是否为外部网络（不由本平台创建）。</param>
public sealed record ComposeNetworkDefinition(
    string Name,
    string? ActualName,
    bool External);

/// <summary>
/// 解析后的顶层卷定义。
/// </summary>
/// <param name="Name">逻辑名（YAML 里的键）。</param>
/// <param name="ActualName">实际卷名；为 <see langword="null"/> 时用 <paramref name="Name"/>。</param>
/// <param name="External">是否为外部卷（不由本平台创建）。</param>
public sealed record ComposeVolumeDefinition(
    string Name,
    string? ActualName,
    bool External);

/// <summary>
/// 解析后的 Compose 文件整体。
/// </summary>
/// <param name="Services">服务定义（按 YAML 中的出现顺序）。</param>
/// <param name="Networks">顶层网络定义。</param>
/// <param name="Volumes">顶层卷定义。</param>
/// <param name="Warnings">解析过程中的提示（如 <c>version</c> 已被废弃）。</param>
public sealed record ComposeDocument(
    IReadOnlyList<ComposeService> Services,
    IReadOnlyList<ComposeNetworkDefinition> Networks,
    IReadOnlyList<ComposeVolumeDefinition> Volumes,
    IReadOnlyList<string> Warnings);
