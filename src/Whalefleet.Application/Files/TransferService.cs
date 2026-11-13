using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 传输任务的对外编排（发起导出、查状态、导出去重）。
/// </summary>
/// <remarks>
/// <para><b>为什么导出去重</b>：同一个镜像被连点两次「导出」时，
/// 两次各自产出一个 GB 级 tar 是纯浪费——磁盘被写满、带宽被占掉，
/// 而用户拿到的内容完全一样。因此这里按「主机 + 资源」复用进行中的任务。</para>
/// <para><b>为什么任务标识由客户端生成</b>：与编排一致——
/// 客户端先生成令牌、在 SignalR 上订阅它，再把它带进请求。
/// 这样「进度事件」与「任务查询」用的是同一个串，不需要服务端再发一次。</para>
/// </remarks>
public sealed class TransferService
{
    private readonly TransferJobRegistry _registry;
    private readonly TransferJobQueue _queue;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentClientIp _clientIp;
    private readonly TimeProvider _timeProvider;
    private readonly FilesOptions _options;
    private readonly ILogger<TransferService> _logger;

    /// <summary>
    /// 初始化传输编排服务。
    /// </summary>
    /// <param name="registry">任务登记表。</param>
    /// <param name="queue">任务队列。</param>
    /// <param name="currentUser">当前登录用户。</param>
    /// <param name="clientIp">来源 IP。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public TransferService(
        TransferJobRegistry registry,
        TransferJobQueue queue,
        ICurrentUser currentUser,
        ICurrentClientIp clientIp,
        TimeProvider timeProvider,
        IOptions<FilesOptions> options,
        ILogger<TransferService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _queue = queue;
        _currentUser = currentUser;
        _clientIp = clientIp;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 发起一次导出（入队后台执行）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="operation">操作类型（见 <see cref="TransferOperations"/>）。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <param name="jobId">任务标识（客户端生成）。</param>
    /// <param name="resourceName">资源名称（审计用快照）。</param>
    /// <returns>发起结果（任务标识与状态）。</returns>
    /// <exception cref="BusinessException">
    /// 资源标识为空（<c>1000</c>）、任务标识已被占用（<c>1000</c>）。
    /// </exception>
    public StartExportResult StartExport(
        string hostId,
        string operation,
        string resourceId,
        string jobId,
        string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // 去重：同一个资源已有进行中的导出时直接复用它的任务。
        var existing = _registry.FindActiveFor(hostId, resourceId);

        if (existing is not null)
        {
            _logger.LogDebug(
                "复用进行中的导出任务 {JobId}（主机 {HostId}，资源 {ResourceId}）",
                existing,
                hostId,
                resourceId);

            return new StartExportResult(existing, TransferJobStatus.Queued, Reused: true);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(_options.ExportTtl);

        var request = new TransferJobRequest(
            jobId,
            operation,
            hostId,
            resourceId,
            resourceName,
            BuildFileName(operation, resourceName),
            _currentUser.UserId,
            _currentUser.Username,
            _clientIp.Ip);

        _registry.Register(request, now, expiresAt);

        if (!_queue.TryEnqueue(request))
        {
            _registry.Abort(jobId);

            throw new BusinessException(
                ErrorCodes.DockerApiFailed,
                "导出队列已关闭，本次导出未被受理。");
        }

        return new StartExportResult(jobId, TransferJobStatus.Queued, Reused: false);
    }

    /// <summary>
    /// 查询导出任务状态。
    /// </summary>
    /// <param name="jobId">任务标识。</param>
    /// <returns>任务状态；不存在返回 <see langword="null"/>。</returns>
    public TransferJobStatusResult? GetStatus(string? jobId)
    {
        var snapshot = _registry.Get(jobId);

        if (snapshot is null)
        {
            return null;
        }

        return new TransferJobStatusResult(
            snapshot.JobId,
            snapshot.Operation,
            snapshot.Status,
            snapshot.Percent,
            snapshot.Step,
            snapshot.ErrorCode,
            snapshot.ErrorMessage,
            snapshot.DownloadUrl,
            snapshot.FileName,
            snapshot.SizeBytes,
            snapshot.ExpiresAt);
    }

    /// <summary>
    /// 构造产物文件名。
    /// </summary>
    /// <param name="operation">操作类型。</param>
    /// <param name="resourceName">资源名称。</param>
    /// <returns>文件名。</returns>
    private static string BuildFileName(string operation, string resourceName)
    {
        var name = ExportArtifactStore.SanitizeFileName(resourceName);

        return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name : name + ".tar";
    }
}
