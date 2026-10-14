using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Docker.DotNet.BasicAuth;
using Docker.DotNet.Handler.Abstractions;
using Docker.DotNet.X509;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Infrastructure.Docker;

/// <summary>
/// 默认 <see cref="IDockerClientFactory"/> 实现：把连接描述构建成 Docker 客户端。
/// </summary>
/// <remarks>
/// <para><b>无状态</b>：本类不缓存任何客户端，每次调用都新建一个（连同新的 HTTP 连接池）。
/// 客户端复用与生命周期管理见 <see cref="DockerClientPool"/>。</para>
/// <para><b>端点先规范化再构建</b>：五花八门的端点写法需要收敛成唯一形式，否则同一守护进程
/// 会被当成多条连接（见 <see cref="DockerEndpointNormalizer"/>）。传输方式由规范化后的
/// scheme 自动推断。</para>
/// </remarks>
public sealed class DockerClientFactory : IDockerClientFactory
{
    private readonly ILogger<DockerClientFactory> _logger;

    /// <summary>
    /// 初始化 <see cref="DockerClientFactory"/>。
    /// </summary>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> 为 <see langword="null"/>。</exception>
    public DockerClientFactory(
        ILogger<DockerClientFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public IDockerClient CreateClient(DockerHostConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!DockerEndpointNormalizer.TryNormalize(
                connection.Type,
                connection.Endpoint,
                out var normalized,
                out var error))
        {
            throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"Docker 主机「{connection.HostId}」的端点配置非法：{error}");
        }

        _logger.LogInformation(
            "构建 Docker 客户端（HostId={HostId}，Connection={Connection}）",
            connection.HostId,
            normalized.CanonicalEndpoint);

        // Docker.DotNet.Enhanced 4.x：DockerClientConfiguration 已被移除，统一改用
        // DockerClientBuilder；传输方式由 endpoint 的 scheme 自动推断。
        var builder = new DockerClientBuilder().WithEndpoint(normalized.EndpointUri);

        var authProvider = this.CreateAuthProvider(connection, normalized);

        if (authProvider is not null)
        {
            builder = builder.WithAuthProvider(authProvider);
        }

        return builder.Build();
    }

    /// <summary>
    /// 按认证方式创建认证提供器。
    /// </summary>
    /// <param name="connection">连接描述（已携带解析后的凭据）。</param>
    /// <param name="normalized">规范化后的端点（用于判断是否走 TLS）。</param>
    /// <returns>认证提供器；无认证时为 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">认证方式不支持，或凭据与端点形态矛盾（<c>1301</c>）。</exception>
    private IAuthProvider? CreateAuthProvider(
        DockerHostConnection connection,
        NormalizedDockerEndpoint normalized)
    {
        var isTls = normalized.EndpointUri.Scheme.Equals(
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

        switch (connection.Auth.Kind)
        {
            case DockerHostAuthKind.None:
                return null;

            case DockerHostAuthKind.Tls:
                if (connection.Auth.Tls is not { } tls)
                {
                    throw new BusinessException(
                        ErrorCodes.HostUnreachable,
                        $"主机「{connection.HostId}」声明了 TLS 认证但未携带凭据");
                }

                if (!isTls)
                {
                    // 明文端点 + 客户端证书：库只在 TLS 通道上使用证书，
                    // 于是配置看起来"启用了 TLS"，实际却是不带认证的明文连接——必须拦下。
                    throw new BusinessException(
                        ErrorCodes.HostUnreachable,
                        $"主机「{connection.HostId}」配置了 TLS 凭据，端点必须写为 https://"
                        + $"（当前为 {normalized.CanonicalEndpoint}）");
                }

                return CreateTlsAuthProvider(connection.HostId, tls);

            case DockerHostAuthKind.Basic:
                if (connection.Auth.Basic is not { } basic)
                {
                    throw new BusinessException(
                        ErrorCodes.HostUnreachable,
                        $"主机「{connection.HostId}」声明了 Basic 认证但未携带凭据");
                }

                if (!isTls)
                {
                    // 不阻断（内网专线上确实有人这么用），但必须留痕：Basic 凭据是可逆的。
                    _logger.LogWarning(
                        "主机 {HostId} 使用 Basic 认证但端点为明文 {Endpoint}："
                        + "凭据在网络中可被还原，建议改用 https://",
                        connection.HostId,
                        normalized.CanonicalEndpoint);
                }

                return new BasicAuthCredentials(
                    basic.Username,
                    basic.Password,
                    tlsEnabled: isTls);

            default:
                throw new BusinessException(
                    ErrorCodes.HostUnreachable,
                    $"主机「{connection.HostId}」的认证方式 {(int)connection.Auth.Kind} 暂不支持");
        }
    }

    /// <summary>
    /// 加载 TLS 证书并组装认证提供器。
    /// </summary>
    /// <param name="hostId">主机标识（用于错误消息）。</param>
    /// <param name="tls">TLS 凭据。</param>
    /// <returns>基于客户端证书的认证提供器。</returns>
    /// <exception cref="BusinessException">证书文件缺失、不可读或内容不是合法证书（<c>1301</c>）。</exception>
    /// <remarks>
    /// <para><b>用 BCL 读 PEM 而不是库的加载方法</b>：库的
    /// <c>LoadCertificateFromPemFiles</c> 不接受口令，加密私钥就无从加载；
    /// <c>X509Certificate2.CreateFromPemFile</c> / <c>CreateFromEncryptedPemFile</c>
    /// 两种情形都覆盖，且不引入额外格式（PFX）支持面。</para>
    /// <para><b>证书对象不显式释放</b>：其生命周期与客户端一致——客户端由池按主机缓存，
    /// 因此进程内数量有界。将来若支持热改凭据，应在让旧条目失效时一并释放。</para>
    /// </remarks>
    private static IAuthProvider CreateTlsAuthProvider(string hostId, DockerTlsCredentials tls)
    {
        var clientCertificate = LoadClientCertificate(hostId, tls);
        var authorityCertificate = LoadAuthorityCertificate(hostId, tls);

        // 用 CA 证书做服务端身份校验回调：不给它就等于接受任意服务端证书。
        return new DockerTlsCertificates(clientCertificate, authorityCertificate).CreateCredentials();
    }

    /// <summary>
    /// 加载客户端证书与私钥（二者必须配套）。
    /// </summary>
    /// <param name="hostId">主机标识（用于错误消息）。</param>
    /// <param name="tls">TLS 凭据。</param>
    /// <returns>带私钥的客户端证书。</returns>
    /// <exception cref="BusinessException">文件缺失、不可读、内容不是 PEM，或私钥与证书不配套（<c>1301</c>）。</exception>
    private static X509Certificate2 LoadClientCertificate(string hostId, DockerTlsCredentials tls)
    {
        try
        {
            return tls.ClientKeyPassword is null
                ? X509Certificate2.CreateFromPemFile(tls.ClientCertificatePath, tls.ClientKeyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(
                    tls.ClientCertificatePath,
                    tls.ClientKeyPassword,
                    tls.ClientKeyPath);
        }
        catch (Exception exception) when (IsCertificateLoadFailure(exception))
        {
            // 消息带上路径：运维要照着改配置。私钥内容与口令不会出现在这里。
            throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"主机「{hostId}」的客户端证书无法加载"
                + $"（certificate={tls.ClientCertificatePath}，key={tls.ClientKeyPath}）：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 加载用于校验服务端证书的 CA 证书。
    /// </summary>
    /// <param name="hostId">主机标识（用于错误消息）。</param>
    /// <param name="tls">TLS 凭据。</param>
    /// <returns>CA 证书（无私钥）。</returns>
    /// <exception cref="BusinessException">文件缺失、不可读或内容不是证书（<c>1301</c>）。</exception>
    /// <remarks>
    /// <b>为什么这里用 <see cref="X509CertificateLoader"/> 而不是 <c>CreateFromPemFile(路径)</c></b>：
    /// 单参重载把「同一个文件」同时当作证书与私钥去找，CA 文件里没有私钥，于是必然报
    /// 「私钥与证书不匹配」——一条指向完全错误方向的报错。CA 只需要证书本体，
    /// 用 <see cref="X509CertificateLoader.LoadCertificateFromFile"/> 更贴切。
    /// </remarks>
    private static X509Certificate2 LoadAuthorityCertificate(string hostId, DockerTlsCredentials tls)
    {
        try
        {
            return X509CertificateLoader.LoadCertificateFromFile(tls.CaCertificatePath);
        }
        catch (Exception exception) when (IsCertificateLoadFailure(exception))
        {
            throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"主机「{hostId}」的 CA 证书无法加载（ca={tls.CaCertificatePath}）：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 这些异常都表示「证书取不到或不可用」：文件系统类与密码学类。
    /// </summary>
    /// <param name="exception">异常。</param>
    /// <returns>属于证书加载失败返回 <see langword="true"/>。</returns>
    private static bool IsCertificateLoadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException;
}
