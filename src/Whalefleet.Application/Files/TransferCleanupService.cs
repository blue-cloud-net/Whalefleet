using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 传输资源的清理后台服务（0.14.0）。
/// </summary>
/// <remarks>
/// <para><b>两件事</b>：① 进程启动时清掉上次遗留的导出产物；
/// ② 周期性地扫掉过期票据与过期任务（连带删除它们的产物文件）。</para>
/// <para><b>为什么启动时必须清</b>：上一次进程崩溃 / 被 kill 时留下的 tar 可能有好几个 GB，
/// 而它们对应的票据与任务登记早已随进程消失——没人会来取走它们，
/// 放着就是白白占着磁盘。启动时清是唯一能保证「不留残留」的时刻
/// （之后没人知道哪些文件是残留的）。</para>
/// <para><b>为什么扫描可以很慢</b>：票据在解析时也会顺带判断是否过期，
/// 因此这里的扫描只是「兜底」——它只负责「没人再碰的产物最终被删掉」。</para>
/// </remarks>
public sealed class TransferCleanupService : BackgroundService
{
    private readonly DownloadTicketStore _tickets;
    private readonly TransferJobRegistry _jobs;
    private readonly ExportArtifactStore _artifacts;
    private readonly TimeProvider _timeProvider;
    private readonly FilesOptions _options;
    private readonly ILogger<TransferCleanupService> _logger;

    /// <summary>
    /// 初始化清理服务。
    /// </summary>
    /// <param name="tickets">下载票据登记表。</param>
    /// <param name="jobs">任务登记表。</param>
    /// <param name="artifacts">导出产物存储。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public TransferCleanupService(
        DownloadTicketStore tickets,
        TransferJobRegistry jobs,
        ExportArtifactStore artifacts,
        TimeProvider timeProvider,
        IOptions<FilesOptions> options,
        ILogger<TransferCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _tickets = tickets;
        _jobs = jobs;
        _artifacts = artifacts;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            this.CleanupLeftovers();
        }
        catch (Exception exception)
        {
            // 启动清理失败不该让宿主起不来：残留文件只是占磁盘，不是致命问题。
            _logger.LogWarning(exception, "清理上次遗留的导出产物失败");
        }

        var interval = _options.SweepInterval > TimeSpan.Zero ? _options.SweepInterval : TimeSpan.FromMinutes(1);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                this.Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
    }

    /// <summary>
    /// 清理上次遗留的导出产物。
    /// </summary>
    private void CleanupLeftovers()
    {
        var directory = _artifacts.EnsureDirectory();
        var removed = _tickets.ClearDirectory(directory);

        if (removed > 0)
        {
            _logger.LogInformation("已清理 {Count} 个上次遗留的导出产物", removed);
        }
    }

    /// <summary>
    /// 扫掉过期票据与过期任务。
    /// </summary>
    private void Sweep()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var tickets = _tickets.Sweep(now);
        var jobs = _jobs.Sweep(now);

        if (tickets > 0 || jobs > 0)
        {
            _logger.LogDebug(
                "清理过期传输资源：票据 {Tickets} 张，任务 {Jobs} 个",
                tickets,
                jobs);
        }
    }
}
