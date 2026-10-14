namespace Whalefleet.Infrastructure.Docker;

/// <summary>
/// Docker 客户端池配置（配置节 <c>Docker</c>）。
/// </summary>
/// <remarks>
/// <para><b>主机不再来自配置</b>（0.0.8 起）：主机是 <c>hosts</c> 表里的数据，由
/// <c>IDockerHostAccessor</c> 读取；首次启动时若表为空，宿主会种入一台默认主机
/// （<c>local</c> / <c>/var/run/docker.sock</c>）。本配置节因此只剩池策略。</para>
/// <para><b>破坏性变更</b>：0.0.1 的 <c>Docker:DefaultHost</c> 与 0.0.2 起的 <c>Docker:Hosts</c>
/// 数组均已取消；原 <c>Docker:ClientCache</c> 更名为 <c>Docker:Pool</c>
/// （环境变量相应变为 <c>DOCKER__POOL__IDLETIMEOUT</c>）。</para>
/// </remarks>
public sealed class DockerClientFactoryOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Docker";

    /// <summary>
    /// 客户端池策略。
    /// </summary>
    public DockerClientPoolOptions Pool { get; set; } = new();
}

/// <summary>
/// Docker 客户端池策略。
/// </summary>
/// <remarks>
/// <para>客户端按连接池化后，长期不用的连接会一直占着 socket 与后台线程，
/// 本策略用于回收这类空闲客户端。</para>
/// <para><b>禁用</b>：把 <see cref="IdleTimeout"/> 或 <see cref="ReapInterval"/> 设为
/// <see cref="TimeSpan.Zero"/>（或负值）即关闭回收，客户端会驻留至进程退出。</para>
/// </remarks>
public sealed class DockerClientPoolOptions
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
