using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Auth;

namespace Whalefleet.Infrastructure.Sessions;

/// <summary>
/// 过期会话清理工作器（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>两条清理路径</b>：① 请求路径发现会话过期 → 入队 → 本工作器立即删；
/// ② 每小时兜底扫描一次，删掉所有过期行。路径 ① 保证「刚过期就消失」，
/// 路径 ② 保证「漏掉的（队列满时丢弃的）也会被补上」。</para>
/// <para><b>自建作用域</b>：后台线程不在任何请求作用域里，借用请求作用域会拿到
/// <b>已释放</b>的作用域——症状是「偶发 ObjectDisposedException」。
/// 因此每个清理批次都自建作用域。</para>
/// <para><b>使用宿主停止令牌</b>：不用请求的 <c>CancellationToken</c>。</para>
/// </remarks>
internal sealed class SessionCleanupWorker(
    SessionCleanupQueue queue,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SessionCleanupWorker> logger) : BackgroundService
{
    /// <summary>
    /// 兜底清理间隔（小时）。
    /// </summary>
    private const int _fallbackIntervalHours = 1;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fallback = this.RunFallbackCleanupAsync(stoppingToken);

        try
        {
            await foreach (var sessionId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await this.DeleteSessionAsync(sessionId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常退出路径。
        }
        finally
        {
            // 兜底任务与主循环共享停止令牌，这里等它收尾。
            await fallback.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 删除单个过期会话。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();

            var repository = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
            var deleted = await repository
                .DeleteByIdsAsync([sessionId], cancellationToken)
                .ConfigureAwait(false);

            if (deleted > 0)
            {
                logger.LogDebug("已清理过期会话 {SessionId}", sessionId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 清理失败只记警告：它不该影响任何请求，更不该让工作器退出。
            logger.LogWarning(exception, "清理过期会话 {SessionId} 失败", sessionId);
        }
    }

    /// <summary>
    /// 周期性的全量兜底清理。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RunFallbackCleanupAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(_fallbackIntervalHours));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();

                    var repository = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
                    var deleted = await repository
                        .DeleteExpiredAsync(timeProvider.GetUtcNow().UtcDateTime, cancellationToken)
                        .ConfigureAwait(false);

                    if (deleted > 0)
                    {
                        logger.LogInformation("已清理 {Count} 条过期会话记录", deleted);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "兜底清理过期会话失败");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常退出路径。
        }
    }
}
