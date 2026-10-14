using Docker.DotNet;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Hosts;
using Microsoft.Extensions.Logging;

namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// 默认 <see cref="IDockerHostAccessor"/> 实现：从 <c>hosts</c> 表解析主机并取得客户端。
/// </summary>
/// <remarks>
/// <para><b>Scoped</b>：依赖 Scoped 的 <see cref="IHostRepository"/>；客户端池是 Singleton，
/// 二者生命周期不同，因此「解析主机」与「池化客户端」分属两层，本类只做转发。</para>
/// <para><b>探活刻意不经池</b>：<see cref="TestConnectionAsync"/> 新建临时客户端并在结束时释放，
/// 以免测试用的连接污染（或延长）生产连接的存活。</para>
/// </remarks>
public sealed class DockerHostAccessor : IDockerHostAccessor
{
    private readonly IHostRepository _hosts;
    private readonly IDockerClientPool _pool;
    private readonly IDockerClientFactory _factory;
    private readonly ILogger<DockerHostAccessor> _logger;

    /// <summary>
    /// 初始化 <see cref="DockerHostAccessor"/>。
    /// </summary>
    /// <param name="hosts">主机仓储（Scoped）。</param>
    /// <param name="pool">客户端池（Singleton）。</param>
    /// <param name="factory">客户端构建器（用于连通性测试）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public DockerHostAccessor(
        IHostRepository hosts,
        IDockerClientPool pool,
        IDockerClientFactory factory,
        ILogger<DockerHostAccessor> logger)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _hosts = hosts;
        _pool = pool;
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DockerHost> ResolveAsync(string hostId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var host = await _hosts.FindByNameAsync(hostId.Trim(), cancellationToken).ConfigureAwait(false);

        return host ?? throw new BusinessException(
            ErrorCodes.HostNotFound,
            $"Docker 主机不存在：{hostId}");
    }

    /// <inheritdoc />
    public async Task<IDockerClient> GetClientAsync(string hostId, CancellationToken cancellationToken = default)
    {
        var host = await this.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        if (!host.Enabled)
        {
            _logger.LogWarning("拒绝取用已停用的 Docker 主机（HostId={HostId}）", host.Name);

            throw new BusinessException(
                ErrorCodes.HostDisabled,
                $"Docker 主机已停用：{host.Name}");
        }

        return _pool.GetOrCreate(DockerHostConnection.From(host));
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        DockerHostConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IDockerClient? client = null;

        try
        {
            client = _factory.CreateClient(connection);
            await client.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BusinessException ex)
        {
            // 端点配置非法也属于「连不通」，按 false 返回而不是抛异常。
            _logger.LogWarning(
                ex,
                "Docker 连通性测试失败：端点配置非法（HostId={HostId}）",
                connection.HostId);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Docker 连通性测试失败（HostId={HostId}）",
                connection.HostId);

            return false;
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Invalidate(DockerHostConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // 直接转发给池：池只认连接描述，不需要（也不该）知道主机名。
        _pool.Invalidate(connection);

        _logger.LogInformation(
            "已让主机客户端失效（HostId={HostId}，Connection={Connection}）",
            connection.HostId,
            // 连接描述里的端点是裸字符串（不是 HostEndpoint），因此不会自动脱敏。
            // 这里显式过一遍 Redact——主机更新/删除都会走到这行，
            // 端点若带 userinfo 就等于又写了一遍明文密码。
            HostEndpoint.Redact(connection.Endpoint));
    }
}
