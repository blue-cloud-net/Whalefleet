namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 主机的连接方式。
/// </summary>
/// <remarks>
/// <para><b>为什么在应用层</b>：本枚举是 <see cref="IDockerClientFactory"/> 与
/// <see cref="DockerHostConnection"/> 的组成部分，而应用层接口<b>不得</b>引用
/// Infrastructure 的类型（依赖方向规则见 <c>docs/architecture.md</c>），
/// 故由 Infrastructure 迁入此处。</para>
/// <para>Windows 命名管道<b>不单列枚举值</b>：以 <c>npipe://</c> 或 <c>\\.\pipe\</c>
/// 形式给出的端点即命名管道，仍归属 <see cref="Socket"/>。</para>
/// </remarks>
public enum DockerHostType
{
    /// <summary>
    /// 本地套接字：Unix socket（Linux/macOS）或 Windows 命名管道。
    /// </summary>
    Socket = 0,

    /// <summary>
    /// 远程 TCP（明文，缺省端口 2375）。TLS / Basic 认证于 0.0.8 落地。
    /// </summary>
    Tcp = 1,
}
