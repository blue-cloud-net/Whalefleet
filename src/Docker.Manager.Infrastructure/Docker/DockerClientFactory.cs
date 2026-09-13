using Docker.DotNet;
using Docker.Manager.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// 默认 <see cref="IDockerClientFactory"/> 实现，基于
/// <see href="https://github.com/testcontainers/Docker.DotNet">Docker.DotNet.Enhanced</see>。
/// </summary>
/// <remarks>
/// <para>懒加载：首次调用 <see cref="GetClientAsync"/> 时才构建 <see cref="IDockerClient"/>，
/// 避免开发机无 Docker socket 时启动期失败。</para>
/// <para>并发安全：使用 <see cref="Lazy{T}(Func{T}, bool)"/> + <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
/// 保证多线程同时请求时仅构建一次。</para>
/// </remarks>
public sealed class DockerClientFactory : IDockerClientFactory, IDisposable
{
    private readonly DockerClientFactoryOptions _options;
    private readonly ILogger<DockerClientFactory> _logger;
    private readonly Lazy<IDockerClient> _client;

    public DockerClientFactory(
        IOptions<DockerClientFactoryOptions> options,
        ILogger<DockerClientFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _client = new Lazy<IDockerClient>(BuildClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public Task<DockerClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        // DockerClient 是同步构建的，但接口按异步约定暴露，便于 0.0.8 多主机场景扩展为按需创建。
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((DockerClient)_client.Value);
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = BuildClient();
            await client.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker 连通性测试失败（Endpoint={Endpoint}）", _options.Endpoint);
            return false;
        }
    }

    private IDockerClient BuildClient()
    {
        Uri endpointUri = _options.Type switch
        {
            DockerHostType.Socket => BuildSocketUri(_options.Endpoint),
            DockerHostType.Tcp => BuildTcpUri(_options.Endpoint),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_options.Type),
                _options.Type,
                "不支持的 DockerHostType"),
        };

        _logger.LogInformation(
            "Docker 客户端构建完成（Type={Type}，Endpoint={Endpoint}）",
            _options.Type,
            endpointUri.ToString());

        var configuration = new DockerClientConfiguration(endpointUri);
        return configuration.CreateClient();
    }

    private static Uri BuildSocketUri(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Socket 端点不能为空", nameof(endpoint));
        }

        // 若已是完整 URI（含 scheme），直接使用；否则补 unix:// 前缀
        return endpoint.Contains("://", StringComparison.Ordinal)
            ? new Uri(endpoint)
            : new Uri("unix://" + endpoint.TrimStart('/'));
    }

    private static Uri BuildTcpUri(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("TCP 端点不能为空", nameof(endpoint));
        }

        return endpoint.Contains("://", StringComparison.Ordinal)
            ? new Uri(endpoint)
            : new Uri("tcp://" + endpoint);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}