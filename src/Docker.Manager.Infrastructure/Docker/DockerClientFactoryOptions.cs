using Docker.Manager.Application.Abstractions;

namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// Docker 主机与客户端缓存配置（配置节 <c>Docker</c>）。
/// </summary>
/// <remarks>
/// <para><b>破坏性变更</b>：0.0.1 的单一主机配置节 <c>Docker:DefaultHost</c> 已被
/// <see cref="Hosts"/> 数组取代（<c>Docker:Hosts:&lt;index&gt;</c>），
/// 环境变量相应变为 <c>DOCKER__HOSTS__0__ENDPOINT</c>。</para>
/// <para>主机名（<see cref="DockerHostOptions.Name"/>）即业务侧的 <c>hostId</c>。
/// 0.0.8 把主机来源换成 <c>hosts</c> 表后，本配置节可退化为「启动时的默认主机种子」。</para>
/// </remarks>
public sealed class DockerClientFactoryOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Docker";

    /// <summary>
    /// 已配置的 Docker 主机列表（至少一项）。
    /// </summary>
    /// <remarks>
    /// 多个主机指向<b>同一端点</b>是合法的：它们共享同一个客户端与连接池
    /// （缓存键是规范化连接串，而非主机名），见 <c>DockerEndpointNormalizer</c>。
    /// </remarks>
    public List<DockerHostOptions> Hosts { get; set; } = [];

    /// <summary>
    /// 客户端缓存策略。
    /// </summary>
    public DockerClientCacheOptions ClientCache { get; set; } = new();
}

/// <summary>
/// 单个 Docker 主机的配置。
/// </summary>
public sealed class DockerHostOptions
{
    /// <summary>
    /// 主机标识（业务侧 <c>hostId</c>），不区分大小写，全局唯一。
    /// </summary>
    public string Name { get; set; } = String.Empty;

    /// <summary>
    /// 连接方式（默认 <see cref="DockerHostType.Socket"/>）。
    /// </summary>
    public DockerHostType Type { get; set; } = DockerHostType.Socket;

    /// <summary>
    /// 端点：socket 路径（如 <c>/var/run/docker.sock</c>）或 TCP 地址（如 <c>tcp://host:2375</c>）。
    /// </summary>
    /// <remarks>
    /// 写法容忍度见 <c>DockerEndpointNormalizer</c>；非法值会在<b>启动时</b>直接失败
    /// （<c>ValidateOnStart</c>），不会拖到第一次连接。
    /// </remarks>
    public string Endpoint { get; set; } = String.Empty;
}

/// <summary>
/// Docker 客户端缓存策略。
/// </summary>
/// <remarks>
/// <para>客户端按连接缓存后，长期不用的连接会一直占着 socket 与后台线程，
/// 本策略用于回收这类空闲客户端。</para>
/// <para><b>禁用</b>：把 <see cref="IdleTimeout"/> 或 <see cref="ReapInterval"/> 设为
/// <see cref="TimeSpan.Zero"/>（或负值）即关闭回收，客户端会驻留至进程退出。</para>
/// </remarks>
public sealed class DockerClientCacheOptions
{
    /// <summary>
    /// 空闲多久后允许回收（默认 30 分钟）。
    /// </summary>
    /// <remarks>
    /// 判定依据是「最近一次取用时间」，因此若某次调用自身的持续时长超过该阈值
    /// （例如 0.0.9 的容器日志流式读取），理论上存在被回收打断的风险。
    /// 遇到这类长调用场景请调大该值，或直接禁用回收。
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 后台回收扫描间隔（默认 5 分钟）。
    /// </summary>
    public TimeSpan ReapInterval { get; set; } = TimeSpan.FromMinutes(5);
}