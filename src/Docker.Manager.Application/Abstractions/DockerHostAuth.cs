using System.Security.Cryptography;
using System.Text;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 主机的认证信息（运行期凭据）。
/// </summary>
/// <remarks>
/// <para><b>运行期凭据</b>：<see cref="Kind"/> 之外的字段承载<b>已解析</b>的凭据本体
/// （TLS 的三份 PEM 路径 / Basic 的账号密码），工厂据此构建认证提供器。</para>
/// <para><b>枚举归属</b>：<see cref="DockerHostAuthKind"/> 定义在
/// <see cref="Docker.Manager.Core.Hosts"/>（与 <c>DockerHost</c> 实体共享取值域），
/// 本记录只承载<b>运行期</b>凭据与缓存区分串。</para>
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
    /// TLS 凭据；仅 <see cref="DockerHostAuthKind.Tls"/> 时非空。
    /// </summary>
    public DockerTlsCredentials? Tls { get; init; }

    /// <summary>
    /// Basic 凭据；仅 <see cref="DockerHostAuthKind.Basic"/> 时非空。
    /// </summary>
    public DockerBasicCredentials? Basic { get; init; }

    /// <summary>
    /// 凭据指纹（参与缓存键的不可逆摘要）。
    /// </summary>
    /// <remarks>
    /// 由「认证方式 + 载荷」的 SHA-256 前 16 字节（小写十六进制）得出。
    /// 不可逆，因此可安全用于缓存键与日志。<see cref="DockerHostAuthKind.None"/> 时为 <see langword="null"/>。
    /// </remarks>
    public string? CredentialFingerprint { get; init; }

    /// <summary>
    /// 参与连接缓存键的「认证区分串」。
    /// </summary>
    /// <remarks>
    /// <para>同一端点在不同凭据下是<b>两条不同的连接</b>，必须落到不同的缓存条目，
    /// 否则切换凭据会错误地复用旧连接。</para>
    /// <para>无认证时为空串；有认证时为<b>不可逆的凭据指纹</b>，
    /// <b>禁止</b>返回密码等敏感原文。</para>
    /// </remarks>
    public string CacheDiscriminator =>
        this.Kind == DockerHostAuthKind.None
            ? String.Empty
            : this.CredentialFingerprint ?? this.Kind.ToString();

    /// <summary>
    /// 由主机凭据映射出运行期认证信息。
    /// </summary>
    /// <param name="credentials">主机凭据值对象。</param>
    /// <returns>无认证时返回共享的 <see cref="None"/>；否则带上已解析的凭据与指纹。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="BusinessException">
    /// 认证方式未知，或凭据载荷不符合该方式的 schema（错误码 <c>1301</c>）。
    /// </exception>
    /// <remarks>
    /// <b>这里报错而不是退化为无认证</b>：凭据读不出来的正确后果是「连不上」而不是「不用密码连上了」。
    /// 静默降级会把一个配置笔误变成安全问题，因此宁可在构建客户端前就失败。
    /// </remarks>
    public static DockerHostAuth From(HostCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return credentials.Kind switch
        {
            DockerHostAuthKind.None => None,
            DockerHostAuthKind.Tls => new DockerHostAuth
            {
                Kind = DockerHostAuthKind.Tls,
                Tls = DockerTlsCredentials.Parse(credentials.ConfigJson),
                CredentialFingerprint = Fingerprint(credentials),
            },
            DockerHostAuthKind.Basic => new DockerHostAuth
            {
                Kind = DockerHostAuthKind.Basic,
                Basic = DockerBasicCredentials.Parse(credentials.ConfigJson),
                CredentialFingerprint = Fingerprint(credentials),
            },
            _ => throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"不支持的认证方式：{(int)credentials.Kind}"),
        };
    }

    private static string Fingerprint(HostCredentials credentials)
    {
        var payload = $"{credentials.Kind}:{credentials.ConfigJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
