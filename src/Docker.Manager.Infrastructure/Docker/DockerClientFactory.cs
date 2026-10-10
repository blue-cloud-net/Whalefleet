using System.Collections.Concurrent;
using Docker.DotNet;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// 默认 <see cref="IDockerClientFactory"/> 实现：按<b>规范化连接串</b>缓存多个客户端。
/// </summary>
/// <remarks>
/// <para><b>缓存键</b>是端点的规范化连接串（必要时再拼认证区分串），<b>不是</b> hostId。
/// 因此两个指向同一守护进程的主机名共享同一个 <see cref="IDockerClient"/> 及其 HTTP 连接池——
/// 切换主机时连接不会被重建，这正是本类存在的意义。</para>
/// <para><b>懒加载</b>：首次取用某条连接时才构建客户端，
/// 避免开发机没有 Docker socket 时启动期就失败。</para>
/// <para><b>并发安全</b>：<see cref="ConcurrentDictionary{TKey,TValue}"/> +
/// <see cref="Lazy{T}(Func{T}, LazyThreadSafetyMode)"/> 保证同一条连接只构建一次。</para>
/// <para><b>构建失败必须清条目</b>：<see cref="Lazy{T}"/> 会把异常永久缓存，
/// 若不把失败条目移出缓存，一次构建失败之后就再也无法重试。</para>
/// <para><b>释放时机</b>：显式 <see cref="Invalidate"/>（配置变更 / 主机删除）、
/// 空闲超时回收（见 <see cref="IDockerClientCacheMaintenance"/>）、或进程退出时由 DI 释放。</para>
/// </remarks>
public sealed class DockerClientFactory : IDockerClientFactory, IDockerClientCacheMaintenance, IDisposable
{
    private readonly DockerClientFactoryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DockerClientFactory> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _clients = new(StringComparer.Ordinal);

    private volatile bool _disposed;

    /// <summary>
    /// 初始化 <see cref="DockerClientFactory"/>。
    /// </summary>
    /// <param name="options">主机与缓存配置。</param>
    /// <param name="timeProvider">时间源（测试可注入假时间以验证空闲回收）。</param>
    /// <param name="logger">日志。</param>
    public DockerClientFactory(
        IOptions<DockerClientFactoryOptions> options,
        TimeProvider timeProvider,
        ILogger<DockerClientFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IDockerClient> GetClientAsync(string hostId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var connection = ResolveConnection(hostId)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"Docker 主机不存在：{hostId}");

        var (cacheKey, normalized) = BuildCacheKey(connection);
        var now = _timeProvider.GetUtcNow();

        while (true)
        {
            var entry = _clients.GetOrAdd(
                cacheKey,
                _ => new CacheEntry(cacheKey, connection, now, () => BuildClient(connection, normalized)));

            // 命中但已空闲超时：这里做「惰性」判定，使回收不必等后台任务的下一次扫描。
            if (IsIdleExpired(entry, now) && _clients.TryRemove(new KeyValuePair<string, CacheEntry>(cacheKey, entry)))
            {
                _logger.LogInformation(
                    "Docker 连接已空闲超时，重建客户端（HostId={HostId}，Connection={Connection}，Idle={Idle}）",
                    connection.HostId,
                    cacheKey,
                    now - entry.LastUsedUtc);

                DisposeEntry(entry);
                continue;
            }

            try
            {
                var client = entry.Client.Value;
                entry.Touch(now);
                return Task.FromResult(client);
            }
            catch
            {
                // Lazy 会永久缓存异常：不移除条目的话，一次构建失败后再也无法重试。
                _clients.TryRemove(new KeyValuePair<string, CacheEntry>(cacheKey, entry));
                DisposeEntry(entry);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        DockerHostConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!DockerEndpointNormalizer.TryNormalize(connection.Type, connection.Endpoint, out var normalized, out var error))
        {
            _logger.LogWarning(
                "Docker 连通性测试失败：端点配置非法（HostId={HostId}，Reason={Reason}）",
                connection.HostId,
                error);

            return false;
        }

        DockerClient? client = null;

        try
        {
            // 刻意不经缓存：测试用的客户端不应污染（也不应延长）生产连接的存活。
            client = BuildClient(connection, normalized);
            await client.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Docker 连通性测试失败（HostId={HostId}，Connection={Connection}）",
                connection.HostId,
                normalized.CanonicalEndpoint);

            return false;
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Invalidate(string hostId)
    {
        if (String.IsNullOrWhiteSpace(hostId))
        {
            return;
        }

        var connection = ResolveConnection(hostId);
        if (connection is null)
        {
            _logger.LogDebug("忽略失效请求：Docker 主机不存在（HostId={HostId}）", hostId);
            return;
        }

        if (!DockerEndpointNormalizer.TryNormalize(connection.Type, connection.Endpoint, out _, out var error))
        {
            _logger.LogDebug(
                "忽略失效请求：端点配置非法（HostId={HostId}，Reason={Reason}）",
                hostId,
                error);

            return;
        }

        var (cacheKey, _) = BuildCacheKey(connection);

        if (_clients.TryRemove(cacheKey, out var entry))
        {
            _logger.LogInformation(
                "Docker 客户端缓存已失效（HostId={HostId}，Connection={Connection}）",
                hostId,
                cacheKey);

            DisposeEntry(entry);
        }
    }

    /// <inheritdoc />
    public int ReapIdleClients()
    {
        if (_options.ClientCache.IdleTimeout <= TimeSpan.Zero)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var reaped = 0;

        foreach (var (cacheKey, entry) in _clients)
        {
            if (!IsIdleExpired(entry, now) || !_clients.TryRemove(new KeyValuePair<string, CacheEntry>(cacheKey, entry)))
            {
                continue;
            }

            _logger.LogInformation(
                "回收空闲 Docker 客户端（HostId={HostId}，Connection={Connection}，Idle={Idle}）",
                entry.Connection.HostId,
                cacheKey,
                now - entry.LastUsedUtc);

            DisposeEntry(entry);
            reaped++;
        }

        return reaped;
    }

    /// <summary>
    /// 释放全部缓存客户端。
    /// </summary>
    /// <remarks>
    /// 由 DI 容器在宿主停机时调用。释放后本实例不再可用
    /// （<see cref="GetClientAsync"/> 会抛 <see cref="ObjectDisposedException"/>）。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var entry in _clients.Values)
        {
            DisposeEntry(entry);
        }

        _clients.Clear();
    }

    private DockerHostConnection? ResolveConnection(string hostId)
    {
        if (String.IsNullOrWhiteSpace(hostId))
        {
            return null;
        }

        foreach (var host in _options.Hosts)
        {
            if (String.Equals(host.Name, hostId, StringComparison.OrdinalIgnoreCase))
            {
                return new DockerHostConnection
                {
                    HostId = host.Name,
                    Type = host.Type,
                    Endpoint = host.Endpoint,
                };
            }
        }

        return null;
    }

    private static (string CacheKey, NormalizedDockerEndpoint Normalized) BuildCacheKey(DockerHostConnection connection)
    {
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

        var discriminator = connection.Auth.CacheDiscriminator;

        // 同端点不同凭据 = 不同连接，必须分开缓存（否则切换凭据会错误复用旧连接）。
        var cacheKey = discriminator.Length == 0
            ? normalized.CanonicalEndpoint
            : $"{normalized.CanonicalEndpoint}|{discriminator}";

        return (cacheKey, normalized);
    }

    private DockerClient BuildClient(DockerHostConnection connection, NormalizedDockerEndpoint normalized)
    {
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

    private bool IsIdleExpired(CacheEntry entry, DateTimeOffset now)
    {
        var idleTimeout = _options.ClientCache.IdleTimeout;

        return idleTimeout > TimeSpan.Zero && now - entry.LastUsedUtc >= idleTimeout;
    }

    private void DisposeEntry(CacheEntry entry)
    {
        if (!entry.Client.IsValueCreated)
        {
            return;
        }

        try
        {
            entry.Client.Value.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 Docker 客户端失败（Connection={Connection}）", entry.ConnectionKey);
        }
    }

    /// <summary>
    /// 一个缓存条目：连接描述 + 懒构建的客户端 + 最近使用时间。
    /// </summary>
    private sealed class CacheEntry
    {
        private long _lastUsedUtcTicks;

        public CacheEntry(
            string connectionKey,
            DockerHostConnection connection,
            DateTimeOffset now,
            Func<IDockerClient> clientFactory)
        {
            ConnectionKey = connectionKey;
            Connection = connection;
            Client = new Lazy<IDockerClient>(clientFactory, LazyThreadSafetyMode.ExecutionAndPublication);
            Touch(now);
        }

        /// <summary>
        /// 规范化连接串（缓存键），用于日志与诊断。
        /// </summary>
        public string ConnectionKey { get; }

        /// <summary>
        /// 首次登记该连接的主机描述（同一连接可能被多个主机名引用）。
        /// </summary>
        public DockerHostConnection Connection { get; }

        /// <summary>
        /// 懒构建的客户端。
        /// </summary>
        public Lazy<IDockerClient> Client { get; }

        /// <summary>
        /// 最近一次被取用的时间（UTC）。用 <see cref="long"/> + <c>Volatile</c> 存取，
        /// 避免 <see cref="DateTimeOffset"/> 非原子读写导致的撕裂值。
        /// </summary>
        public DateTimeOffset LastUsedUtc => new(Volatile.Read(ref _lastUsedUtcTicks), TimeSpan.Zero);

        /// <summary>
        /// 标记为「刚刚使用」。
        /// </summary>
        /// <param name="now">当前时间。</param>
        public void Touch(DateTimeOffset now) => Volatile.Write(ref _lastUsedUtcTicks, now.UtcTicks);
    }
}
