using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Stacks;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排任务后台工作器：从队列里取出操作并执行。
/// </summary>
/// <remarks>
/// <para><b>为什么每个任务自建作用域</b>：执行器依赖 <b>Scoped</b> 的服务
/// （主机访问器、镜像用例、审计仓储…），而后台线程<b>不在任何请求作用域里</b>。
/// 借用请求作用域会拿到一个已释放的作用域，
/// 因此这里每个任务开一个新作用域，让依赖的生命周期与任务对齐。</para>
/// <para><b>为什么用 <see cref="CancellationToken.None"/> 执行任务</b>：
/// 宿主停止时的取消令牌会在进程关闭时触发——
/// 而「用户点了部署，进程正要关」正是最需要把这个部署做完的时刻
/// （半途而废的部署会留下「建了一半」的资源）。
/// 因此任务本身不受宿主停止影响；宿主停止只终止「不再接新任务」。</para>
/// <para><b>单个任务失败不终止循环</b>：失败只标记该任务，
/// 循环继续处理下一个——一个坏 YAML 不该让整个编排功能从此罢工。</para>
/// </remarks>
public sealed class StackOperationWorker : BackgroundService
{
    private readonly StackOperationQueue _queue;
    private readonly StackTaskRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StackOperationWorker> _logger;

    /// <summary>
    /// 初始化工作器。
    /// </summary>
    /// <param name="queue">操作队列。</param>
    /// <param name="registry">任务登记表。</param>
    /// <param name="scopeFactory">依赖注入作用域工厂。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public StackOperationWorker(
        StackOperationQueue queue,
        StackTaskRegistry registry,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<StackOperationWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // 任务用 None 执行：宿主关闭不该把用户刚点的部署打断在半路
                await this.ProcessAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：正常路径。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "编排任务工作循环异常退出");
        }
    }

    /// <summary>
    /// 执行一个任务。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task ProcessAsync(StackOperationRequest request)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _registry.MarkRunning(request.TaskId, "开始执行", now);

        // 作用域提到 try 之外：失败路径也要写审计，而那一刻作用域还在
        using var scope = _scopeFactory.CreateScope();

        try
        {
            var executor = scope.ServiceProvider.GetRequiredService<StackOperationExecutor>();
            var result = await executor.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);

            var finished = _timeProvider.GetUtcNow().UtcDateTime;

            _registry.MarkSucceeded(request.TaskId, result, [], finished);

            await this.RemoveRecordAsync(scope.ServiceProvider, request).ConfigureAwait(false);

            await this.RecordAuditAsync(scope.ServiceProvider, request, true, null, result, finished)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "编排任务 {TaskId} 完成：主机 {HostName}，编排 {StackName}，操作 {Operation}",
                request.TaskId,
                request.HostName,
                request.StackName,
                request.Operation);
        }
        catch (BusinessException exception)
        {
            var failed = _timeProvider.GetUtcNow().UtcDateTime;

            _registry.MarkFailed(request.TaskId, exception.Code, exception.Message, failed);

            await this.RecordAuditAsync(
                scope.ServiceProvider,
                request,
                false,
                exception,
                null,
                failed).ConfigureAwait(false);

            await this.NotifyFailureAsync(request, exception.Code, exception.Message, failed)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "编排任务 {TaskId} 失败：主机 {HostName}，编排 {StackName}，操作 {Operation}，原因 {Reason}",
                request.TaskId,
                request.HostName,
                request.StackName,
                request.Operation,
                exception.Message);
        }
        catch (Exception exception)
        {
            var failed = _timeProvider.GetUtcNow().UtcDateTime;

            var failure = new BusinessException(
                ErrorCodes.StackDeployFailed,
                ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.StackDeployFailed));
            _registry.MarkFailed(request.TaskId, failure.Code, failure.Message, failed);

            await this.RecordAuditAsync(scope.ServiceProvider, request, false, failure, null, failed)
                .ConfigureAwait(false);
            await this.NotifyFailureAsync(request, failure.Code, failure.Message, failed)
                .ConfigureAwait(false);

            _logger.LogError(
                exception,
                "编排任务 {TaskId} 异常退出：主机 {HostName}，编排 {StackName}",
                request.TaskId,
                request.HostName,
                request.StackName);
        }
    }

    /// <summary>
    /// 删除操作成功后清掉数据库里的 Stack 记录。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">操作请求。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <para><b>为什么放在「执行成功之后」而不是请求期</b>：删除失败时记录必须还在——
    /// 否则用户会看到「这个编排被删了」，而容器还在跑，下一次「应用」又会把它拉起来，
    /// 变成一个用户完全看不懂的循环。</para>
    /// <para><b>为什么只在删除操作里做</b>：其余操作（部署 / 更新 / 停止）都要留着 YAML，
    /// 否则就失去了「更新」的能力。</para>
    /// <para><b>清记录失败不影响任务结果</b>：容器与网络已经清掉了，
    /// 只是列表里多一条「受管但空」的条目——那是可以事后修的，
    /// 而把整个任务标成失败会让用户以为删除没成功（而它其实成功了）。</para>
    /// </remarks>
    private async Task RemoveRecordAsync(IServiceProvider services, StackOperationRequest request)
    {
        if (request.Operation != StackOperations.Remove)
        {
            return;
        }

        try
        {
            var repository = services.GetRequiredService<IStackRepository>();
            var hosts = services.GetRequiredService<IHostRepository>();

            var host = await hosts.FindByNameAsync(request.HostName).ConfigureAwait(false);

            if (host is null)
            {
                return;
            }

            var entity = await repository
                .FindByHostAndNameAsync(host.Id, request.StackName)
                .ConfigureAwait(false);

            if (entity is null)
            {
                return;
            }

            await repository.RemoveByIdAsync(entity.Id).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "删除编排记录失败（主机={HostName}，编排={StackName}）",
                request.HostName,
                request.StackName);
        }
    }

    /// <summary>
    /// 写一条审计。
    /// </summary>
    /// <param name="services">作用域的服务提供器。</param>
    /// <param name="request">操作请求。</param>
    /// <param name="succeeded">是否成功。</param>
    /// <param name="exception">失败时的异常。</param>
    /// <param name="result">成功时的结果摘要。</param>
    /// <param name="occurredAt">发生时刻（UTC）。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>审计写不进去不该让任务变成「失败」</b>：任务已经执行完了，
    /// 审计只是留痕。因此这里吞掉审计异常（<c>IAuditLogger</c> 本身也是 best-effort）。
    /// </remarks>
    private async Task RecordAuditAsync(
        IServiceProvider services,
        StackOperationRequest request,
        bool succeeded,
        BusinessException? exception,
        StackOperationResult? result,
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
                    TargetType = AuditTargetTypes.Stack,
                    TargetId = request.StackName,
                    TargetName = request.StackName,
                    HostName = request.HostName,
                    ErrorCode = exception?.Code,
                    ClientIp = request.ClientIp,
                    Detail = BuildDetail(request, result),
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception auditException)
        {
            _logger.LogWarning(
                auditException,
                "写编排任务审计失败（任务 {TaskId}）",
                request.TaskId);
        }
    }

    /// <summary>
    /// 解析审计动作。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <returns>审计动作标识。</returns>
    /// <remarks>
    /// <b>Webhook 判定必须排在 <c>update</c> 之前</b>：webhook 触发的重放
    /// 用的就是 <c>update</c> 操作，若按操作类型先判，
    /// 审计里记的就是普通更新——「谁触发的这次部署」这条线索就丢了。
    /// </remarks>
    private static string ResolveAuditAction(StackOperationRequest request) => request.Operation switch
    {
        _ when request.WebhookTokenHint is not null => AuditActions.StackWebhookReload,
        StackOperations.Update => AuditActions.StackUpdate,
        StackOperations.Stop => AuditActions.StackStop,
        StackOperations.Remove => AuditActions.StackRemove,
        _ => AuditActions.StackDeploy,
    };

    /// <summary>
    /// 拼审计的附加说明。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="result">结果摘要。</param>
    /// <returns>说明文本。</returns>
    /// <remarks>
    /// <b>不写环境变量的值、不写 YAML 全文</b>：那里面常含口令，
    /// 而审计日志会被采集器上报到集中式系统。
    /// </remarks>
    private static string BuildDetail(StackOperationRequest request, StackOperationResult? result)
    {
        var parts = new List<string>
        {
            $"操作={request.Operation}",
            $"拉取策略={(request.PullAlways ? "always" : "missing")}",
        };

        if (request.PruneOrphans)
        {
            parts.Add("清理孤儿=是");
        }

        if (request.WebhookTokenHint is not null)
        {
            parts.Add($"来源=webhook({request.WebhookTokenHint})");
        }

        if (result is not null)
        {
            parts.Add($"新建={result.Created}");
            parts.Add($"重建={result.Recreated}");
            parts.Add($"清理={result.Removed}");
        }

        return String.Join("，", parts);
    }

    /// <summary>
    /// 推送失败通知。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="errorCode">业务错误码。</param>
    /// <param name="message">失败原因。</param>
    /// <param name="occurredAt">发生时刻（UTC）。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task NotifyFailureAsync(
        StackOperationRequest request,
        int errorCode,
        string message,
        DateTime occurredAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var notifier = scope.ServiceProvider.GetRequiredService<IStackTaskNotifier>();

            await notifier.NotifyFailedAsync(
                new StackTaskFailureNotification(
                    request.HostName,
                    request.StackName,
                    request.Operation,
                    errorCode,
                    message,
                    occurredAt),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "推送编排任务失败通知时出错（任务 {TaskId}）", request.TaskId);
        }
    }
}
