using System.Collections.Concurrent;
using Docker.DotNet;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Infrastructure.Docker;

/// <summary>
/// 默认 <see cref="IDockerClientPool"/> 实现：按「规范化连接串」池化 Docker 客户端。
/// </summary>
/// <remarks>
/// <para><b>池键</b>是端点的规范化连接串（必要时再拼认证区分串）。因此两个指向同一守护进程的
/// 主机名共享同一个 <see cref="IDockerClient"/> 及其 HTTP 连接池——切换主机时连接不会被重建，
/// 这正是本类存在的意义。</para>
/// <para><b>池化装的是 OS 资源</b>（socket + 后台线程 + 底层 HTTP 连接池），因此有收缩空闲、
/// 容量天然有界（= 主机数）与集中释放三项「池」的特征，而不是「缓存」那种可随意过期重建的语义。</para>
/// <para><b>并发安全</b>：<see cref="ConcurrentDictionary{TKey,TValue}"/> +
/// <see cref="Lazy{T}"/> 保证同一条连接只构建一次。</para>
/// <para><b>构建失败必须清条目</b>：<see cref="Lazy{T}"/> 会把异常永久缓存，
/// 若不把失败条目移出池，一次构建失败之后就再也无法重试。</para>
/// <para><b>释放时机</b>：显式 <see cref="Invalidate"/>（主机配置变更 / 主机删除）、
/// 空闲超时回收（见 <see cref="IDockerClientPoolMaintenance"/>）、或进程退出时由 DI 释放。</para>
/// </remarks>
public sealed class DockerClientPool : IDockerClientPool, IDockerClientPoolMaintenance, IDisposable
{
    private readonly IDockerClientFactory _factory;
    private readonly DockerClientFactoryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DockerClientPool> _logger;
    private readonly ConcurrentDictionary<string, PoolEntry> _clients = new(StringComparer.Ordinal);

    private volatile bool _disposed;

    /// <summary>
    /// 初始化 <see cref="DockerClientPool"/>。
    /// </summary>
    /// <param name="factory">客户端构建器（无状态）。</param>
    /// <param name="options">池策略配置。</param>
    /// <param name="timeProvider">时间源（测试可注入假时间以验证空闲回收）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public DockerClientPool(
        IDockerClientFactory factory,
        IOptions<DockerClientFactoryOptions> options,
        TimeProvider timeProvider,
        ILogger<DockerClientPool> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDockerClient GetOrCreate(DockerHostConnection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connection);

        var poolKey = BuildPoolKey(connection);
        var now = _timeProvider.GetUtcNow();

        while (true)
        {
            var entry = _clients.GetOrAdd(
                poolKey,
                _ => new PoolEntry(poolKey, connection, now, () => _factory.CreateClient(connection)));

            // 命中但已空闲超时：这里做「惰性」判定，使回收不必等后台任务的下一次扫描。
            if (this.IsIdleExpired(entry, now) && _clients.TryRemove(new KeyValuePair<string, PoolEntry>(poolKey, entry)))
            {
                _logger.LogInformation(
                    "Docker 连接已空闲超时，重建客户端（HostId={HostId}，Connection={Connection}，Idle={Idle}）",
                    connection.HostId,
                    poolKey,
                    now - entry.LastUsedUtc);

                this.DisposeEntry(entry);
                continue;
            }

            try
            {
                var client = entry.Client.Value;
                entry.Touch(now);
                return client;
            }
            catch
            {
                // Lazy 会永久缓存异常：不移除条目的话，一次构建失败后再也无法重试。
                _clients.TryRemove(new KeyValuePair<string, PoolEntry>(poolKey, entry));
                this.DisposeEntry(entry);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Invalidate(DockerHostConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string poolKey;

        try
        {
            poolKey = BuildPoolKey(connection);
        }
        catch (BusinessException ex)
        {
            _logger.LogDebug(ex, "忽略失效请求：连接描述非法（HostId={HostId}）", connection.HostId);
            return;
        }

        if (_clients.TryRemove(poolKey, out var entry))
        {
            _logger.LogInformation(
                "Docker 客户端池条目已失效（HostId={HostId}，Connection={Connection}）",
                connection.HostId,
                poolKey);

            this.DisposeEntry(entry);
        }
    }

    /// <inheritdoc />
    public int ReapIdleClients()
    {
        if (_options.Pool.IdleTimeout <= TimeSpan.Zero)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var reaped = 0;

        foreach (var (poolKey, entry) in _clients)
        {
            if (!this.IsIdleExpired(entry, now) || !_clients.TryRemove(new KeyValuePair<string, PoolEntry>(poolKey, entry)))
            {
                continue;
            }

            _logger.LogInformation(
                "回收空闲 Docker 客户端（HostId={HostId}，Connection={Connection}，Idle={Idle}）",
                entry.Connection.HostId,
                poolKey,
                now - entry.LastUsedUtc);

            this.DisposeEntry(entry);
            reaped++;
        }

        return reaped;
    }

    /// <summary>
    /// 释放池内全部客户端。
    /// </summary>
    /// <remarks>
    /// 由 DI 容器在宿主停机时调用。释放后本实例不再可用
    /// （<see cref="GetOrCreate"/> 会抛 <see cref="ObjectDisposedException"/>）。
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
            this.DisposeEntry(entry);
        }

        _clients.Clear();
    }

    private static string BuildPoolKey(DockerHostConnection connection)
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

        // 同端点不同凭据 = 不同连接，必须分开池化（否则切换凭据会错误复用旧连接）。
        return discriminator.Length == 0
            ? normalized.CanonicalEndpoint
            : $"{normalized.CanonicalEndpoint}|{discriminator}";
    }

    private bool IsIdleExpired(PoolEntry entry, DateTimeOffset now)
    {
        var idleTimeout = _options.Pool.IdleTimeout;

        return idleTimeout > TimeSpan.Zero && now - entry.LastUsedUtc >= idleTimeout;
    }

    private void DisposeEntry(PoolEntry entry)
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
            _logger.LogWarning(ex, "释放 Docker 客户端失败（Connection={Connection}）", entry.PoolKey);
        }
    }

    /// <summary>
    /// 一个池条目：连接描述 + 懒构建的客户端 + 最近使用时间。
    /// </summary>
    private sealed class PoolEntry
    {
        private long _lastUsedUtcTicks;

        public PoolEntry(
            string poolKey,
            DockerHostConnection connection,
            DateTimeOffset now,
            Func<IDockerClient> clientFactory)
        {
            this.PoolKey = poolKey;
            this.Connection = connection;
            this.Client = new Lazy<IDockerClient>(clientFactory, LazyThreadSafetyMode.ExecutionAndPublication);
            this.Touch(now);
        }

        /// <summary>
        /// 规范化连接串（池键），用于日志与诊断。
        /// </summary>
        public string PoolKey { get; }

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
