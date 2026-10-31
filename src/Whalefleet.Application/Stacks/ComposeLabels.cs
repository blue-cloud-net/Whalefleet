namespace Whalefleet.Application.Stacks;

/// <summary>
/// Compose 标签常量：<c>com.docker.compose.*</c> 系列的键名。
/// </summary>
/// <remarks>
/// <para><b>这些标签是「Stack 与容器之间唯一的联结</b>：本系统不额外维护
/// 「哪个容器属于哪个 Stack」的表——那会与守护进程的事实产生第二份真相，
/// 而容器随时可能被用户在命令行删掉或重建。</para>
/// <para><b>与官方 <c>docker compose</c> 保持一致</b>：标签名与语义照抄 Compose，
/// 因此用户在同一台机器上用 <c>docker compose ps</c> 也能认出本平台部署的项目，
/// 反过来我们也能认出行命令部署的项目（那类标 <c>managed=false</c>）。</para>
/// <para><b><c>config-hash</c> 是「要不要重建」的判据</b>：Compose 用服务配置内容的哈希
/// 标记容器，「哈希变了就重建」正是它的行为。我们沿用同一套语义——
/// 否则每次应用都会重建全部容器，用户会看到毫无变化的服务被换掉。</para>
/// </remarks>
internal static class ComposeLabels
{
    /// <summary>项目名标签（值 = Compose 项目名）。</summary>
    public const string Project = "com.docker.compose.project";

    /// <summary>服务名标签（值 = 服务名）。</summary>
    public const string Service = "com.docker.compose.service";

    /// <summary>容器序号标签（值 = 项目内第几个实例，从 1 开始）。</summary>
    public const string ContainerNumber = "com.docker.compose.container-number";

    /// <summary>服务配置哈希标签（值 = 该服务配置的哈希）。</summary>
    public const string ConfigHash = "com.docker.compose.config-hash";

    /// <summary>一次性任务标记（值 = <c>True</c> / <c>False</c>）。</summary>
    /// <remarks>
    /// Compose 用它标记「跑完就删」的一次性容器（<c>docker compose run</c>）。
    /// 本平台不产生这类容器，但外部 compose 项目可能有——
    /// 列表时必须跳过它们，否则「服务列表」里会多出已经结束的一次性容器。
    /// </remarks>
    public const string OneOff = "com.docker.compose.oneoff";

    /// <summary>配置文件路径标签（值 = YAML 文件路径，多个用逗号分隔）。</summary>
    /// <remarks>
    /// 仅作标识用：本平台不读这个路径（YAML 原文存在库里），
    /// 但写上它能让 <c>docker compose</c> 侧认出这个项目是「有配置文件的」，
    /// 从而把 <c>docker compose ps</c> 与 <c>docker compose down</c> 也接上。
    /// </remarks>
    public const string ConfigFiles = "com.docker.compose.project.config_files";

    /// <summary>工作目录标签（值 = 项目的工作目录）。</summary>
    public const string WorkingDir = "com.docker.compose.project.working_dir";

    /// <summary>镜像拉取策略的取值：只在本地缺失时拉取。</summary>
    public const string PullPolicyMissing = "missing";

    /// <summary>镜像拉取策略的取值：总是拉取最新。</summary>
    public const string PullPolicyAlways = "always";
}
