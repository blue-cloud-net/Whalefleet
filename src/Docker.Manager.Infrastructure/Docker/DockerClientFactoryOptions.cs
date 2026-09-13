namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// Docker 主机连接配置。
/// </summary>
/// <remarks>
/// <para>对应配置节 <c>Docker:DefaultHost</c>（0.0.8 扩展为 <c>Docker:Hosts:&lt;name&gt;</c> 数组）。</para>
/// <list type="bullet">
///   <item><description><see cref="Type"/> = <see cref="DockerHostType.Socket"/> 时，<see cref="Endpoint"/> 例如
///   <c>/var/run/docker.sock</c>（Linux/macOS）或 <c>//./pipe/docker_engine</c>（Windows）。</description></item>
///   <item><description><see cref="Type"/> = <see cref="DockerHostType.Tcp"/> 时，<see cref="Endpoint"/> 为
///   <c>tcp://host:port</c>。TLS / Basic 认证字段在 0.0.8 落地。</description></item>
/// </list>
/// </remarks>
public sealed class DockerClientFactoryOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Docker:DefaultHost";

    /// <summary>
    /// Docker 主机类型（默认 Socket）。
    /// </summary>
    public DockerHostType Type { get; set; } = DockerHostType.Socket;

    /// <summary>
    /// Docker 端点 URL 或 Unix socket / Named pipe 路径。
    /// </summary>
    public string Endpoint { get; set; } = "/var/run/docker.sock";
}

/// <summary>
/// Docker 主机类型。
/// </summary>
public enum DockerHostType
{
    /// <summary>
    /// Unix socket（Linux/macOS）或 Named pipe（Windows）。
    /// </summary>
    Socket = 0,

    /// <summary>
    /// 远程 TCP（可选 TLS / Basic 认证，0.0.8 落地）。
    /// </summary>
    Tcp = 1,
}