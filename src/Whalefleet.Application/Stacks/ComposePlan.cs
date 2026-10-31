namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排计划：项目要建哪些资源、按什么顺序建。
/// </summary>
/// <param name="ProjectName">项目名（= Stack 名称）。</param>
/// <param name="Networks">网络计划。</param>
/// <param name="Volumes">具名卷计划。</param>
/// <param name="Services">服务计划。</param>
/// <param name="StartOrder">启动顺序（服务名，依赖在前）。</param>
public sealed record ComposePlan(
    string ProjectName,
    IReadOnlyList<ComposeNetworkPlan> Networks,
    IReadOnlyList<ComposeVolumePlan> Volumes,
    IReadOnlyList<ComposeServicePlan> Services,
    IReadOnlyList<string> StartOrder);

/// <summary>
/// 网络计划。
/// </summary>
/// <param name="Name">逻辑名（YAML 里的键）。</param>
/// <param name="ActualName">实际网络名。</param>
/// <param name="External">是否为外部网络（不由本平台创建）。</param>
public sealed record ComposeNetworkPlan(
    string Name,
    string ActualName,
    bool External);

/// <summary>
/// 具名卷计划。
/// </summary>
/// <param name="Name">逻辑名（YAML 里的键）。</param>
/// <param name="ActualName">实际卷名。</param>
/// <param name="External">是否为外部卷（不由本平台创建）。</param>
public sealed record ComposeVolumePlan(
    string Name,
    string ActualName,
    bool External);

/// <summary>
/// 服务计划。
/// </summary>
/// <param name="Service">解析后的服务定义。</param>
/// <param name="ContainerName">容器名（<c>&lt;项目名&gt;-&lt;服务名&gt;-1</c>）。</param>
/// <param name="ConfigHash">配置哈希（变了才重建）。</param>
/// <param name="Networks">接入的网络（逻辑名）。</param>
/// <param name="Volumes">具名卷挂载。</param>
/// <param name="Binds">宿主路径挂载。</param>
public sealed record ComposeServicePlan(
    ComposeService Service,
    string ContainerName,
    string ConfigHash,
    IReadOnlyList<string> Networks,
    IReadOnlyList<ComposeServiceVolume> Volumes,
    IReadOnlyList<ComposeServiceBind> Binds);

/// <summary>
/// 服务对具名卷的挂载。
/// </summary>
/// <param name="LogicalName">逻辑卷名（YAML 里的键）。</param>
/// <param name="ActualName">实际卷名。</param>
/// <param name="Destination">容器内路径。</param>
/// <param name="ReadOnly">是否只读。</param>
public sealed record ComposeServiceVolume(
    string LogicalName,
    string ActualName,
    string Destination,
    bool ReadOnly);

/// <summary>
/// 服务对宿主路径的挂载。
/// </summary>
/// <param name="Source">宿主路径。</param>
/// <param name="Destination">容器内路径。</param>
/// <param name="ReadOnly">是否只读。</param>
public sealed record ComposeServiceBind(
    string Source,
    string Destination,
    bool ReadOnly);
