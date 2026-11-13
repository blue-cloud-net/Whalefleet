using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Application.Files;

/// <summary>
/// 传输任务后台工作器：从队列里取出导出任务并执行。
/// </summary>
/// <remarks>
/// <para><b>为什么每个任务自建作用域</b>：执行器依赖 <b>Scoped</b> 的服务
/// （主机访问器、镜像用例、审计仓储…），而后台线程<b>不在任何请求作用域里</b>。
/// 借用请求作用域会拿到一个已释放的作用域，
/// 因此这里每个任务开一个新作用域，让依赖的生命周期与任务对齐。</para>
/// <para><b>为什么用 <see cref="CancellationToken.None"/> 执行任务</b>：
/// 宿主停止时的取消令牌会在进程关闭时触发——
/// 而「用户点了导出，进程正要关」正是最需要把这个导出做完的时刻
/// （半途而废的导出会留下一个半截的 GB 级文件）。
/// 因此任务本身不受宿主停止影响；宿主停止只终止「不再接新任务」。</para>
/// <para><b>单个任务失败不终止循环</b>：失败只标记该任务，
/// 循环继续处理下一个——一个坏镜像不该让整个导出功能从此罢工。</para>
/// <para><b>并发上限</b>：读者循环的数量由 <c>Files:MaxConcurrentExports</c> 决定。
/// 导出是 IO 密集型，并发太多会互相抢磁盘与带宽。</para>
/// </remarks>
public sealed class TransferJobWorker : BackgroundService
{
    private readonly TransferJobQueue _queue;
    private readonly TransferJobRegistry _registry;
    private readonly DownloadTicketStore _tickets;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly FilesOptions _options;
    private readonly ILogger<TransferJobWorker> _logger;

    /// <summary>
    /// 初始化工作器。
    /// </summary>
    /// <param name="queue">任务队列。</param>
    /// <param name="registry">任务登记表。</param>
    /// <param name="tickets">下载票据登记表。</param>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public TransferJobWorker(
        TransferJobQueue queue,
        TransferJobRegistry registry,
        DownloadTicketStore tickets,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<FilesOptions> options,
        ILogger<TransferJobWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _registry = registry;
        _tickets = tickets;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, _options.MaxConcurrentExports);
        var readers = new Task[concurrency];

        for (var index = 0; index < concurrency; index++)
        {
            readers[index] = this.ReadLoopAsync(stoppingToken);
        }

        try
        {
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "传输任务工作循环异常退出");
        }
    }

    /// <summary>
    /// 读取循环：从队列取任务并逐个处理。
    /// </summary>
    /// <param name="stoppingToken">宿主停止令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task ReadLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // 任务用 None 执行：宿主关闭不该把用户刚点的导出打断在半路
                await this.ProcessAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
    }

    /// <summary>
    /// 执行一个导出任务。
    /// </summary>
    /// <param name="request">任务请求。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task ProcessAsync(TransferJobRequest request)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _registry.MarkRunning(request.JobId, "开始导出", now);

        // 作用域提到 try 之外：失败路径也要写审计，而那一刻作用域还在
        using var scope = _scopeFactory.CreateScope();

        try
        {
            var artifact = await this.RunExportAsync(scope.ServiceProvider, request).ConfigureAwait(false);
            var finished = _timeProvider.GetUtcNow().UtcDateTime;

            var ticket = _tickets.Issue(
                artifact.FilePath,
                artifact.FileName,
                artifact.SizeBytes,
                request.HostId,
                request.Operation.StartsWith("image", StringComparison.Ordinal) ? "image" : "container",
                request.ResourceId,
                finished);

            var downloadUrl = BuildDownloadUrl(request, ticket.Token);

            _registry.MarkSucceeded(request.JobId, downloadUrl, artifact.FileName, artifact.SizeBytes, finished);

            await this.RecordAuditAsync(scope.ServiceProvider, request, true, null, artifact, finished)
                .ConfigureAwait(false);

            await this.PublishTerminalAsync(scope.ServiceProvider, request, 100, "导出完成", null).ConfigureAwait(false);

            _logger.LogInformation(
                "导出任务 {JobId} 完成：主机 {HostId}，资源 {ResourceId}，大小 {Size} 字节",
                request.JobId,
                request.HostId,
                request.ResourceId,
                artifact.SizeBytes);
        }
        catch (BusinessException exception)
        {
            var failed = _timeProvider.GetUtcNow().UtcDateTime;

            _registry.MarkFailed(request.JobId, exception.Code, exception.Message, failed);

            await this.RecordAuditAsync(scope.ServiceProvider, request, false, exception, null, failed)
                .ConfigureAwait(false);

            await this.PublishTerminalAsync(scope.ServiceProvider, request, null, "导出失败", exception.Message)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "导出任务 {JobId} 失败：主机 {HostId}，资源 {ResourceId}，原因 {Reason}",
                request.JobId,
                request.HostId,
                request.ResourceId,
                exception.Message);
        }
        catch (Exception exception)
        {
            var failed = _timeProvider.GetUtcNow().UtcDateTime;

            var failure = new BusinessException(
                ErrorCodes.DockerApiFailed,
                ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.DockerApiFailed));

            _registry.MarkFailed(request.JobId, failure.Code, failure.Message, failed);

            await this.RecordAuditAsync(scope.ServiceProvider, request, false, failure, null, failed)
                .ConfigureAwait(false);

            await this.PublishTerminalAsync(scope.ServiceProvider, request, null, "导出失败", failure.Message)
                .ConfigureAwait(false);

            _logger.LogError(
                exception,
                "导出任务 {JobId} 异常退出：主机 {HostId}，资源 {ResourceId}",
                request.JobId,
                request.HostId,
                request.ResourceId);
        }
    }

    /// <summary>
    /// 按操作类型分派到具体的导出实现。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">任务请求。</param>
    /// <returns>导出产物。</returns>
    /// <exception cref="BusinessException">
    /// 操作类型未知（<c>1000</c>）。
    /// </exception>
    private async Task<TransferArtifact> RunExportAsync(
        IServiceProvider services,
        TransferJobRequest request)
    {
        var publisher = services.GetRequiredService<IOperationProgressPublisher>();
        var progress = new TransferProgressReporter(
            (percent, step) => this.ReportProgressAsync(services, request, publisher, percent, step));

        var artifact = await DispatchAsync(services, request, progress).ConfigureAwait(false);

        // 等进度链发完：否则「导出完成」的终态事件可能比最后一条进度先到。
        await progress.Completion.ConfigureAwait(false);

        return artifact;
    }

    /// <summary>
    /// 按操作类型分派到具体的导出实现。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">任务请求。</param>
    /// <param name="progress">进度回报。</param>
    /// <returns>导出产物。</returns>
    /// <exception cref="BusinessException">
    /// 操作类型未知（<c>1000</c>）。
    /// </exception>
    private static async Task<TransferArtifact> DispatchAsync(
        IServiceProvider services,
        TransferJobRequest request,
        IProgress<TransferProgress> progress)
    {
        switch (request.Operation)
        {
            case TransferOperations.ImageExport:
            {
                var service = services.GetRequiredService<ImageTransferService>();
                return await service.ExportAsync(request.HostId, request.ResourceId, progress, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            case TransferOperations.ContainerExport:
            {
                var service = services.GetRequiredService<ContainerTransferService>();
                return await service.ExportAsync(request.HostId, request.ResourceId, progress, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            default:
                throw new BusinessException(
                    ErrorCodes.ValidationFailed,
                    $"未知的传输操作类型「{request.Operation}」");
        }
    }

    /// <summary>
    /// 汇报进度（登记表 + 实时通道）。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">任务请求。</param>
    /// <param name="publisher">进度发布器。</param>
    /// <param name="percent">总体进度百分比。</param>
    /// <param name="step">当前步骤描述。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task ReportProgressAsync(
        IServiceProvider services,
        TransferJobRequest request,
        IOperationProgressPublisher publisher,
        double? percent,
        string step)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _registry.ReportProgress(request.JobId, percent, step, now);

        try
        {
            await publisher.PublishAsync(
                new OperationProgress(request.JobId, request.Operation, percent, step, Done: false, Error: null))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 推送失败不该让导出失败——客户端可能已经关掉了页面。
            _logger.LogDebug(exception, "推送导出进度失败（任务 {JobId}）", request.JobId);
        }
    }

    /// <summary>
    /// 推送一个终态进度（完成或失败）。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">任务请求。</param>
    /// <param name="percent">总体百分比。</param>
    /// <param name="status">状态文案。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task PublishTerminalAsync(
        IServiceProvider services,
        TransferJobRequest request,
        double? percent,
        string status,
        string? error)
    {
        try
        {
            var publisher = services.GetRequiredService<IOperationProgressPublisher>();

            await publisher.PublishAsync(
                new OperationProgress(
                    request.JobId,
                    request.Operation,
                    percent,
                    status,
                    Done: error is null,
                    Error: error)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "推送导出终态进度失败（任务 {JobId}）", request.JobId);
        }
    }

    /// <summary>
    /// 写审计。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">任务请求。</param>
    /// <param name="succeeded">是否成功。</param>
    /// <param name="exception">失败时的异常。</param>
    /// <param name="artifact">成功时的产物。</param>
    /// <param name="occurredAt">发生时刻（UTC）。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>审计写不进去不该让任务变成「失败」</b>：导出已经完成了，
    /// 审计只是留痕。因此这里吞掉审计异常（<c>IAuditLogger</c> 本身也是 best-effort）。
    /// </remarks>
    private async Task RecordAuditAsync(
        IServiceProvider services,
        TransferJobRequest request,
        bool succeeded,
        BusinessException? exception,
        TransferArtifact? artifact,
        DateTime occurredAt)
    {
        try
        {
            var audit = services.GetRequiredService<IAuditLogger>();

            await audit.RecordAsync(
                new AuditLogDraft
                {
                    Category = AuditCategory.Activity,
                    Action = ResolveAuditAction(request),
                    Outcome = succeeded ? AuditOutcome.Success : AuditOutcome.Failure,
                    OccurredAt = occurredAt,
                    ActorId = request.ActorId,
                    ActorName = request.ActorName ?? String.Empty,
                    TargetType = request.Operation.StartsWith("image", StringComparison.Ordinal)
                        ? AuditTargetTypes.Image
                        : AuditTargetTypes.Container,
                    TargetId = request.ResourceId,
                    TargetName = request.ResourceName,
                    HostName = request.HostId,
                    ErrorCode = exception?.Code,
                    ClientIp = request.ClientIp,
                    Detail = succeeded
                        ? $"导出成功；大小={artifact?.SizeBytes ?? 0} 字节"
                        : $"导出失败；原因={exception?.Message}",
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception auditException)
        {
            _logger.LogWarning(
                auditException,
                "写导出任务审计失败（任务 {JobId}）",
                request.JobId);
        }
    }

    /// <summary>
    /// 解析审计动作。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <returns>审计动作标识。</returns>
    private static string ResolveAuditAction(TransferJobRequest request) =>
        request.Operation == TransferOperations.ContainerExport
            ? AuditActions.ContainerExport
            : AuditActions.ImageExport;

    /// <summary>
    /// 构造下载地址（相对路径）。
    /// </summary>
    /// <param name="request">任务请求。</param>
    /// <param name="token">下载票据。</param>
    /// <returns>下载地址。</returns>
    /// <remarks>
    /// 地址里带 <c>hostId</c> 是为了与其它主机作用域端点保持同构——
    /// 但那条路径会经过主机可见性校验，而票据本身已经是凭证了。
    /// 因此下载端点由 <c>[DownloadTicket]</c> 元数据跳过主机解析，
    /// 主机名在路径里只是「看起来一致」，不承担鉴权职责。
    /// </remarks>
    private static string BuildDownloadUrl(TransferJobRequest request, string token) =>
        $"/api/v1/hosts/{Uri.EscapeDataString(request.HostId)}/" +
        $"{(request.Operation.StartsWith("image", StringComparison.Ordinal) ? "images" : "containers")}" +
        $"/download?token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// 把进度回调适配成 <see cref="IProgress{T}"/>。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么要串行化</b>：<see cref="IProgress{T}.Report"/> 是<b>同步</b>的
    /// （返回 <see langword="void"/>），而推送到实时通道是异步的。
    /// 直接把异步方法塞进去会得到「调用结果未被等待」的警告，
    /// 而且事件可能<b>乱序</b>发出（百分比会来回跳）。</para>
    /// <para>因此这里把每次上报<b>接在上一条后面</b>：顺序保证，
    /// 而调用方（导出实现）不必 await 任何东西。最后由工作器统一
    /// <see cref="Completion"/> 收尾。</para>
    /// </remarks>
    private sealed class TransferProgressReporter : IProgress<TransferProgress>
    {
        private readonly Func<double?, string, Task> _handler;
        private Task _tail = Task.CompletedTask;

        /// <summary>
        /// 初始化进度回报器。
        /// </summary>
        /// <param name="handler">实际处理进度的委托。</param>
        public TransferProgressReporter(Func<double?, string, Task> handler)
        {
            _handler = handler;
        }

        /// <summary>
        /// 已排队但尚未发完的进度（工作器在导出结束后等它）。
        /// </summary>
        public Task Completion => _tail;

        /// <inheritdoc />
        public void Report(TransferProgress value)
        {
            var handler = _handler;
            var percent = value.Percent;
            var step = value.Step;

            _tail = _tail.ContinueWith(
                _ => handler(percent, step),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }
}
