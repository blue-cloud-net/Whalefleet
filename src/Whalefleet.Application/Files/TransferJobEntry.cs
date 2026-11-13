namespace Whalefleet.Application.Files;

/// <summary>
/// 登记表里的一条任务记录（可变，供后台工作线程推进）。
/// </summary>
/// <remarks>
/// <para><b>为什么用类而不是 record</b>：record 是不可变的，
/// 而任务状态要**原地推进**（排队 → 执行中 → 完成）。
/// 每次推进都换一个新 record 替换进字典，在高并发下反而更容易出现
/// 「读到的快照与刚写进去的不一致」。</para>
/// <para><b>线程安全</b>：写来自后台工作线程，读来自 HTTP 请求线程。
/// 这里用 <see cref="lock"/> 保护字段读写——更新都是「读几个字段、改、整体替换」，
/// 加锁的成本远低于让读者看到半更新状态。</para>
/// </remarks>
internal sealed class TransferJobEntry
{
    private readonly object _gate = new();

    private string _status = TransferJobStatus.Queued;
    private double? _percent;
    private string? _step;
    private int? _errorCode;
    private string? _errorMessage;
    private string? _downloadUrl;
    private string? _fileName;
    private long? _sizeBytes;
    private DateTime? _finishedAt;
    private string? _artifactPath;

    /// <summary>
    /// 初始化任务记录。
    /// </summary>
    /// <param name="request">任务请求。</param>
    /// <param name="now">创建时刻（UTC）。</param>
    /// <param name="expiresAt">过期时刻（UTC）。</param>
    public TransferJobEntry(TransferJobRequest request, DateTime now, DateTime expiresAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        JobId = request.JobId;
        Operation = request.Operation;
        HostId = request.HostId;
        ResourceId = request.ResourceId;
        ResourceName = request.ResourceName;
        _fileName = request.FileName;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    /// <summary>任务标识。</summary>
    public string JobId { get; }

    /// <summary>操作类型。</summary>
    public string Operation { get; }

    /// <summary>所属主机名。</summary>
    public string HostId { get; }

    /// <summary>资源标识。</summary>
    public string ResourceId { get; }

    /// <summary>资源名称。</summary>
    public string ResourceName { get; }

    /// <summary>创建时刻（UTC）。</summary>
    public DateTime CreatedAt { get; }

    /// <summary>过期时刻（UTC）。</summary>
    public DateTime ExpiresAt { get; }

    /// <summary>当前状态。</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>任务是否已结束。</summary>
    public bool IsTerminal
    {
        get
        {
            lock (_gate)
            {
                return _status is TransferJobStatus.Succeeded or TransferJobStatus.Failed;
            }
        }
    }

    /// <summary>
    /// 标记任务开始执行。
    /// </summary>
    /// <param name="step">当前步骤描述。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkRunning(string? step, DateTime now)
    {
        lock (_gate)
        {
            _status = TransferJobStatus.Running;
            _step = step;
            _percent = null;
            _finishedAt = null;
        }
    }

    /// <summary>
    /// 汇报进度。
    /// </summary>
    /// <param name="percent">总体进度百分比；无法确定时为 <see langword="null"/>。</param>
    /// <param name="step">当前步骤描述。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void ReportProgress(double? percent, string? step, DateTime now)
    {
        lock (_gate)
        {
            _percent = percent;
            _step = step;
        }
    }

    /// <summary>
    /// 标记成功。
    /// </summary>
    /// <param name="downloadUrl">下载地址（相对路径）。</param>
    /// <param name="fileName">产物文件名。</param>
    /// <param name="sizeBytes">产物大小（字节）。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkSucceeded(string? downloadUrl, string? fileName, long? sizeBytes, DateTime now)
    {
        lock (_gate)
        {
            _status = TransferJobStatus.Succeeded;
            _downloadUrl = downloadUrl;
            _fileName = fileName;
            _sizeBytes = sizeBytes;
            _percent = 100;
            _step = "导出完成";
            _finishedAt = now;
        }
    }

    /// <summary>
    /// 标记失败。
    /// </summary>
    /// <param name="errorCode">业务错误码。</param>
    /// <param name="errorMessage">失败原因。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkFailed(int errorCode, string errorMessage, DateTime now)
    {
        lock (_gate)
        {
            _status = TransferJobStatus.Failed;
            _errorCode = errorCode;
            _errorMessage = errorMessage;
            _step = "导出失败";
            _finishedAt = now;
        }
    }

    /// <summary>
    /// 记录产物文件路径（供过期清理时删除）。
    /// </summary>
    /// <param name="artifactPath">产物路径。</param>
    public void SetArtifact(string artifactPath)
    {
        lock (_gate)
        {
            _artifactPath = artifactPath;
        }
    }

    /// <summary>
    /// 删除产物文件（失败只记日志）。
    /// </summary>
    public void DeleteArtifact()
    {
        string? path;

        lock (_gate)
        {
            path = _artifactPath;
            _artifactPath = null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉不是致命问题：文件最多留在磁盘上到下次启动清理。
            // 抛出去反而会让「任务过期清理」变成一次 500。
        }
    }

    /// <summary>
    /// 取快照。
    /// </summary>
    /// <returns>任务快照。</returns>
    public TransferJobSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TransferJobSnapshot(
                JobId,
                Operation,
                HostId,
                ResourceId,
                ResourceName,
                _status,
                _percent,
                _step,
                _errorCode,
                _errorMessage,
                _downloadUrl,
                _fileName,
                _sizeBytes,
                CreatedAt,
                _finishedAt,
                ExpiresAt);
        }
    }
}
