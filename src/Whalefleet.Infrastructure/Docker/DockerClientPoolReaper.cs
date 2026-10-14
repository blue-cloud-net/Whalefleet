using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Infrastructure.Docker;

/// <summary>
/// 空闲 Docker 客户端回收后台任务。
/// </summary>
/// <remarks>
/// <para>与池内的「取用时惰性判定」互补：惰性判定只在下次取用该连接时才生效，
/// 若某主机长时间不再被访问，其连接会一直占着 socket 与后台线程。</para>
/// <para><b>禁用</b>：<c>Docker:Pool:IdleTimeout</c> 或 <c>ReapInterval</c> ≤ 0 时，
/// 本任务只记一条日志后立即退出。</para>
/// <para>回收的详细日志由 <see cref="DockerClientPool.ReapIdleClients"/> 输出，此处不重复记录。</para>
/// </remarks>
public sealed class DockerClientPoolReaper : BackgroundService
{
    private readonly IDockerClientPoolMaintenance _maintenance;
    private readonly DockerClientFactoryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DockerClientPoolReaper> _logger;

    /// <summary>
    /// 初始化 <see cref="DockerClientPoolReaper"/>。
    /// </summary>
    /// <param name="maintenance">池的运维操作（由 <see cref="DockerClientPool"/> 实现）。</param>
    /// <param name="options">池策略配置。</param>
    /// <param name="timeProvider">时间源（驱动 <see cref="PeriodicTimer"/>，便于测试）。</param>
    /// <param name="logger">日志。</param>
    public DockerClientPoolReaper(
        IDockerClientPoolMaintenance maintenance,
        IOptions<DockerClientFactoryOptions> options,
        TimeProvider timeProvider,
        ILogger<DockerClientPoolReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _maintenance = maintenance;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pool = _options.Pool;

        if (pool.IdleTimeout <= TimeSpan.Zero || pool.ReapInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Docker 客户端空闲回收已禁用（IdleTimeout={IdleTimeout}，ReapInterval={ReapInterval}）",
                pool.IdleTimeout,
                pool.ReapInterval);

            return;
        }

        _logger.LogInformation(
            "Docker 客户端空闲回收已启动（IdleTimeout={IdleTimeout}，ReapInterval={ReapInterval}）",
            pool.IdleTimeout,
            pool.ReapInterval);

        using var timer = new PeriodicTimer(pool.ReapInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _maintenance.ReapIdleClients();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停机：正常退出路径。
        }
    }
}
