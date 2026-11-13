using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像更新扫描工作器：按周期检查全部启用的策略，自动模式下顺带应用。
/// </summary>
/// <remarks>
/// <para><b>周期 + 抖动</b>：周期由
/// <see cref="ImageUpdatesOptions.ScanIntervalSeconds"/> 决定，首轮前再随机等
/// <see cref="ImageUpdatesOptions.ScanJitterSeconds"/> 秒——多实例部署时不抖动会让
/// 所有实例在同一秒把请求打向仓库，而单实例下它也避免「重启程序」固定落在整点。</para>
/// <para><b>每轮自建作用域</b>：工作器是 Singleton，仓储与用例是 Scoped。
/// 借用宿主作用域会拿到已释放的实例（见 AGENTS.md 陷阱 72）。</para>
/// <para><b><c>CancellationToken.None</c> 用在「应用」那一段</b>：用户点了自动更新，
/// 恰好这时进程要重启——半途而废会留下「拉了一半、容器重建了一半」的现场。
/// 停止信号只终止「不再开新一轮扫描」。</para>
/// <para><b>best-effort</b>：单条策略失败只记日志与通知，循环继续。</para>
/// </remarks>
public sealed class ImageUpdateScanWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<ImageUpdatesOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ImageUpdateScanWorker> _logger;

    /// <summary>
    /// 初始化工作器。
    /// </summary>
    /// <param name="scopes">作用域工厂。</param>
    /// <param name="options">镜像更新选项。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ImageUpdateScanWorker(
        IServiceScopeFactory scopes,
        IOptions<ImageUpdatesOptions> options,
        TimeProvider clock,
        ILogger<ImageUpdateScanWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.Enabled)
        {
            _logger.LogInformation("镜像自动更新扫描已禁用（ImageUpdates:Enabled=false）");

            return;
        }

        if (!options.IsValid())
        {
            _logger.LogError("镜像自动更新配置非法，扫描不会启动（请检查 ImageUpdates 配置节）");

            return;
        }

        var interval = TimeSpan.FromSeconds(options.ScanIntervalSeconds);
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, options.ScanJitterSeconds + 1));

        _logger.LogInformation(
            "镜像自动更新扫描已启动：周期 {Interval}，首轮抖动 {Jitter}",
            interval,
            jitter);

        try
        {
            await Task.Delay(jitter, _clock, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(interval, _clock);

            do
            {
                await this.ScanOnceAsync(options.MaxPoliciesPerScan, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
    }

    /// <summary>
    /// 执行一轮扫描。
    /// </summary>
    /// <param name="maxPolicies">单轮上限。</param>
    /// <param name="stoppingToken">停止令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task ScanOnceAsync(int maxPolicies, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();

            var service = scope.ServiceProvider.GetRequiredService<ImageUpdateService>();

            var summary = await service.ScanAsync(maxPolicies, stoppingToken).ConfigureAwait(false);

            _logger.LogInformation(
                "镜像更新扫描完成：检查 {Checked}，有新版本 {Updated}，已应用 {Applied}，失败 {Failed}",
                summary.Checked,
                summary.Updated,
                summary.Applied,
                summary.Failed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // 一轮失败不该让整个循环退出——下一轮仍会尝试。
            _logger.LogError(error, "镜像更新扫描失败");
        }
    }
}
