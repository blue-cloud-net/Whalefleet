namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 主机的认证方式。
/// </summary>
/// <remarks>
/// 0.0.8 在引入 <c>Docker.DotNet.Enhanced.X509</c> / <c>.BasicAuth</c> 后扩展
/// <c>Tls</c> 与 <c>Basic</c> 两种取值；当前仅有无认证一种。
/// </remarks>
public enum DockerHostAuthKind
{
    /// <summary>
    /// 无认证（本地 socket，或明文 TCP）。
    /// </summary>
    None = 0,
}

/// <summary>
/// Docker 主机的认证信息。
/// </summary>
/// <remarks>
/// <para><b>当前为占位类型</b>：只有 <see cref="None"/> 一种取值。放在这里是为了让
/// <see cref="DockerHostConnection"/> 与 <see cref="IDockerClientFactory"/> 的签名
/// 在 0.0.8 引入证书 / 账号密码时<b>无需再变</b>。</para>
/// <para><see cref="CacheDiscriminator"/> 的语义见 <see cref="DockerHostConnection"/> 的备注。</para>
/// </remarks>
public sealed record DockerHostAuth
{
    /// <summary>
    /// 无认证。
    /// </summary>
    public static DockerHostAuth None { get; } = new();

    /// <summary>
    /// 认证方式。
    /// </summary>
    public DockerHostAuthKind Kind { get; init; } = DockerHostAuthKind.None;

    /// <summary>
    /// 参与连接缓存键的「认证区分串」。
    /// </summary>
    /// <remarks>
    /// <para>同一端点在不同凭据下是<b>两条不同的连接</b>，必须落到不同的缓存条目，
    /// 否则切换凭据会错误地复用旧连接。</para>
    /// <para>无认证时为空串；0.0.8 起应返回<b>不可逆的凭据指纹</b>
    /// （如证书指纹、用户名的哈希），<b>禁止</b>返回密码等敏感原文。</para>
    /// </remarks>
    public string CacheDiscriminator =>
        Kind == DockerHostAuthKind.None ? String.Empty : Kind.ToString();
}
