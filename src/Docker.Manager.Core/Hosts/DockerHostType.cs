namespace Docker.Manager.Core.Hosts;

/// <summary>
/// Docker 主机的连接方式。
/// </summary>
/// <remarks>
/// <para><b>为什么定义在 Core</b>：本枚举同时是核心层 <see cref="DockerHost"/> 实体的属性取值域，
/// 以及应用层 <c>IDockerClientFactory</c> / <c>DockerHostConnection</c> 的组成部分。
/// <c>Core</c> 是最内层、被所有层依赖的项目，把共享取值域放这里可以避免出现两个同名枚举
/// （应用层不得引用 Infrastructure，反之 Core 也不得引用 Application）。</para>
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
