using Docker.DotNet;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Microsoft.Extensions.Logging;

namespace Docker.Manager.Infrastructure.Docker;

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
        return new DockerClientBuilder()
            .WithEndpoint(normalized.EndpointUri)
            .Build();
    }
}
