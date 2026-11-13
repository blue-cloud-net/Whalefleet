using System.Collections.Concurrent;

using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 传输任务登记表：记住「有哪些导出任务在跑、跑到哪一步了、产物在哪」。
/// </summary>
/// <remarks>
/// <para><b>为什么放内存而不是建表</b>：任务登记表服务的是「一次操作的状态」，
/// 而操作的产物（tar 文件）在磁盘上、状态源是守护进程。
/// 登记表只回答「刚才那次请求跑到哪了」——
/// 而这个问题的有效期就是导出那几分钟。
/// 落库会带来两个新问题：过期数据要清理、重启后要恢复半截状态。</para>
/// <para><b>重启丢任务的后果可接受</b>：进程重启时正在跑的导出本来就断了，
/// 产物是半截的；下次用户再点一次导出即可。</para>
/// <para><b>与编排任务登记表的区别</b>：编排登记表靠「同一 Compose 项目同时只有一个任务」
/// 来避免互相踩；而导出是<b>只读</b>操作（<c>docker save</c> / <c>docker export</c>），
/// 多个导出并行不会互相影响，因此这里<b>不</b>做互斥，
/// 改用「并发上限」控制资源占用。</para>
/// </remarks>
public sealed class TransferJobRegistry
{
    /// <summary>
    /// 登记表里最多保留的任务数。
    /// </summary>
    /// <remarks>
    /// 超出时淘汰最旧的<b>已结束</b>任务。正在跑的任务不淘汰——
    /// 淘汰掉等于让客户端永远等不到结果。
    /// </remarks>
    public const int MaxJobs = 100;

    private readonly ConcurrentDictionary<string, TransferJobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// 登记一个新任务。
    /// </summary>
    /// <param name="request">任务请求。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <param name="expiresAt">产物与票据的过期时刻（UTC）。</param>
    /// <exception cref="BusinessException">
    /// 任务标识已被占用（<c>1000</c>）。
    /// </exception>
    public void Register(TransferJobRequest request, DateTime now, DateTime expiresAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_jobs.ContainsKey(request.JobId))
            {
                throw new BusinessException(
                    ErrorCodes.ValidationFailed,
                    $"任务标识「{request.JobId}」已被占用，请换一个");
            }

            _jobs[request.JobId] = new TransferJobEntry(request, now, expiresAt);

            if (_jobs.Count > MaxJobs)
            {
                this.EvictOldest(now);
            }
        }
    }

    /// <summary>
    /// 撤销一个<b>尚未开始执行</b>的任务登记。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <returns>撤销成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 登记之后还有几步可能失败（写库、参数校验）。
    /// 若失败后不撤销，这个任务标识会被占住到 TTL 结束，
    /// 用户看着「已有进行中的导出」却根本没有任何任务在跑。
    /// </remarks>
    public bool Abort(string jobId)
    {
        if (String.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
            {
                return false;
            }

            if (entry.IsTerminal || entry.Status != TransferJobStatus.Queued)
            {
                return false;
            }

            return _jobs.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// 取任务快照。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <returns>快照；不存在返回 <see langword="null"/>。</returns>
    public TransferJobSnapshot? Get(string? jobId)
    {
        if (String.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        return _jobs.TryGetValue(jobId, out var entry) ? entry.Snapshot() : null;
    }

    /// <summary>
    /// 标记任务开始执行。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <param name="step">当前步骤描述。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkRunning(string jobId, string? step, DateTime now)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.MarkRunning(step, now);
        }
    }

    /// <summary>
    /// 汇报任务进度。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <param name="percent">总体进度百分比；无法确定时为 <see langword="null"/>。</param>
    /// <param name="step">当前步骤描述。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void ReportProgress(string jobId, double? percent, string? step, DateTime now)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.ReportProgress(percent, step, now);
        }
    }

    /// <summary>
    /// 标记任务成功。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <param name="downloadUrl">下载地址（相对路径）。</param>
    /// <param name="fileName">产物文件名。</param>
    /// <param name="sizeBytes">产物大小（字节）。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkSucceeded(
        string jobId,
        string? downloadUrl,
        string? fileName,
        long? sizeBytes,
        DateTime now)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.MarkSucceeded(downloadUrl, fileName, sizeBytes, now);
        }
    }

    /// <summary>
    /// 标记任务失败。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <param name="errorCode">业务错误码。</param>
    /// <param name="errorMessage">失败原因。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkFailed(string jobId, int errorCode, string errorMessage, DateTime now)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            entry.MarkFailed(errorCode, errorMessage, now);
        }
    }

    /// <summary>
    /// 清理过期任务（并删除它们指向的产物文件）。
    /// </summary>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <returns>清理掉的任务数。</returns>
    public int Sweep(DateTime now)
    {
        var removed = 0;

        foreach (var pair in _jobs)
        {
            if (pair.Value.ExpiresAt > now)
            {
                continue;
            }

            if (_jobs.TryRemove(pair.Key, out var entry))
            {
                entry.DeleteArtifact();
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// 登记表里当前有多少个<b>未结束</b>的任务。
    /// </summary>
    /// <returns>未结束的任务数。</returns>
    public int CountActive()
    {
        lock (_gate)
        {
            return _jobs.Values.Count(entry => !entry.IsTerminal);
        }
    }

    /// <summary>
    /// 找出一个针对同一资源的<b>未结束</b>任务。
    /// </summary>
    /// <param name="hostId">主机名。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <returns>任务标识；没有返回 <see langword="null"/>。</returns>
    public string? FindActiveFor(string hostId, string resourceId)
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(entry =>
                    !entry.IsTerminal
                    && String.Equals(entry.HostId, hostId, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(entry.ResourceId, resourceId, StringComparison.Ordinal))
                .OrderBy(entry => entry.CreatedAt)
                .Select(entry => entry.JobId)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// 登记表超限时淘汰最旧的已结束任务。
    /// </summary>
    /// <param name="now">当前时刻（UTC）。</param>
    private void EvictOldest(DateTime now)
    {
        lock (_gate)
        {
            var candidates = _jobs.Values
                .Where(entry => entry.IsTerminal)
                .OrderBy(entry => entry.CreatedAt)
                .Take(_jobs.Count - MaxJobs + 1)
                .ToArray();

            foreach (var entry in candidates)
            {
                if (_jobs.TryRemove(entry.JobId, out var removed))
                {
                    removed.DeleteArtifact();
                }
            }
        }
    }
}
