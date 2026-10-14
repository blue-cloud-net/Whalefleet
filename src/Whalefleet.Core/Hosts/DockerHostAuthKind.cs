namespace Whalefleet.Core.Hosts;

/// <summary>
/// Docker 主机的认证方式。
/// </summary>
/// <remarks>
/// <para><b>为什么定义在 Core</b>：它是 <see cref="DockerHostCredentials"/> 值对象与实体
/// <see cref="DockerHost"/> 的组成部分，同时被应用层的 <c>DockerHostAuth</c> 复用；
/// 详见 <see cref="DockerHostType"/> 的同类说明。</para>
/// <para>0.0.8 在引入 <c>Docker.DotNet.Enhanced.X509</c> / <c>.BasicAuth</c> 后启用
/// <see cref="Tls"/> 与 <see cref="Basic"/> 两种取值；当前仅有无认证一种。</para>
/// </remarks>
public enum DockerHostAuthKind
{
    /// <summary>
    /// 无认证（本地 socket，或明文 TCP）。
    /// </summary>
    None = 0,

    /// <summary>
    /// TLS 双向认证（客户端证书 + CA）。0.0.8 落地。
    /// </summary>
    Tls = 1,

    /// <summary>
    /// HTTP Basic 认证（用户名 + 密码）。0.0.8 落地。
    /// </summary>
    Basic = 2,
}
