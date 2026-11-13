using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Operations;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Webhooks;

namespace Whalefleet.Application.Webhooks;

/// <summary>
/// 容器重建 Webhook 的令牌信息。
/// </summary>
/// <param name="ContainerName">目标容器名。</param>
/// <param name="Enabled">是否已启用。</param>
/// <param name="TokenHint">令牌提示（明文的前若干位）。</param>
/// <param name="Token">令牌明文；<b>只在生成 / 轮换的那一刻有值</b>，之后一律为 <see langword="null"/>。</param>
/// <param name="LastUsedAt">最近一次触发时间（UTC）。</param>
public sealed record ContainerWebhookInfo(
    string ContainerName,
    bool Enabled,
    string? TokenHint,
    string? Token,
    DateTime? LastUsedAt);

/// <summary>
/// 容器重建 Webhook 的列表项。
/// </summary>
/// <param name="ContainerName">目标容器名。</param>
/// <param name="Enabled">是否已启用。</param>
/// <param name="TokenHint">令牌提示（明文的前若干位）。</param>
/// <param name="LastUsedAt">最近一次触发时间（UTC）。</param>
public sealed record ContainerWebhookSummary(
    string ContainerName,
    bool Enabled,
    string TokenHint,
    DateTime? LastUsedAt);

/// <summary>
/// 容器重建 Webhook 的目标容器请求。
/// </summary>
public sealed record ContainerWebhookContainerRequest
{
    /// <summary>
    /// 目标容器名；必填。
    /// </summary>
    public string? ContainerName { get; init; }
}

/// <summary>
/// 容器重建 Webhook 的触发请求。
/// </summary>
public sealed record ContainerWebhookReloadRequest
{
    /// <summary>
    /// 重建后是否启动；省略为 <see langword="true"/>（沿用原容器的运行状态）。
    /// </summary>
    public bool? Start { get; init; }

    /// <summary>
    /// 是否同步等待任务结束；省略为 <see langword="false"/>。
    /// </summary>
    public bool? Wait { get; init; }
}

/// <summary>
/// 容器重建 Webhook 用例：令牌的生成 / 轮换 / 吊销 / 列举，以及「由令牌触发的重建」。
/// </summary>
/// <remarks>
/// <para><b>端点是匿名的</b>：凭据就是 URL 里的令牌，主机与容器都由令牌反查得到。
/// 因此路径里<b>不能</b>带 <c>hostId</c>——那会让 <c>CurrentHostMiddleware</c>
/// 拿路由值去查库并做可见性校验，而此时令牌还没验证。</para>
/// <para><b>令牌无效一律 404</b>（<c>1911</c>）：不区分「不存在」「已吊销」「未启用」——
/// 区分了就成了令牌探测工具。</para>
/// <para><b>限流是必需的</b>：一次触发就是一次容器重建，
/// 没有限流，一个写错的 CI 循环会把守护进程打满。</para>
/// </remarks>
public sealed class ContainerWebhookService
{
    /// <summary>
    /// 令牌明文的字节数（32 字节 → Base64url 43 字符）。
    /// </summary>
    private const int _tokenSizeBytes = 32;

    /// <summary>
    /// 令牌提示的长度（明文的前若干位）。
    /// </summary>
    private const int _tokenHintLength = 8;

    /// <summary>
    /// 同步等待任务结束的上限（秒）。
    /// </summary>
    /// <remarks>
    /// 反代的读超时（nginx 默认 60s）比它小一点，
    /// 这样「等不到结果」时是我们自己返回 202 + 任务快照，而不是被反代掐成 504。
    /// </remarks>
    private const int _waitTimeoutSeconds = 45;

    /// <summary>
    /// 同步等待时的轮询间隔（毫秒）。
    /// </summary>
    private const int _waitPollIntervalMs = 250;

    private readonly IContainerWebhookRepository _repository;
    private readonly IHostRepository _hosts;
    private readonly ResourceTaskRegistry<ContainerRecreateResult> _tasks;
    private readonly ResourceOperationQueue _queue;
    private readonly WebhookRateLimiter _rateLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContainerWebhookService> _logger;

    /// <summary>
    /// 初始化容器 Webhook 用例。
    /// </summary>
    /// <param name="repository">Webhook 仓储。</param>
    /// <param name="hosts">主机仓储。</param>
    /// <param name="tasks">任务登记表。</param>
    /// <param name="queue">任务队列。</param>
    /// <param name="rateLimiter">跨请求共享的限流器。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerWebhookService(
        IContainerWebhookRepository repository,
        IHostRepository hosts,
        ResourceTaskRegistry<ContainerRecreateResult> tasks,
        ResourceOperationQueue queue,
        WebhookRateLimiter rateLimiter,
        TimeProvider timeProvider,
        ILogger<ContainerWebhookService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _hosts = hosts;
        _tasks = tasks;
        _queue = queue;
        _rateLimiter = rateLimiter;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 列出某台主机上的全部容器 Webhook。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Webhook 列表（按容器名升序）。</returns>
    public async Task<IReadOnlyList<ContainerWebhookSummary>> ListAsync(
        string hostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var host = await _hosts.FindByNameAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostId}」不存在");

        var webhooks = await _repository.ListByHostAsync(host.Id, cancellationToken).ConfigureAwait(false);

        return [.. webhooks.Select(webhook => new ContainerWebhookSummary(
            webhook.ContainerName,
            webhook.WebhookEnabled,
            webhook.WebhookTokenHint,
            webhook.WebhookLastUsedAt))];
    }

    /// <summary>
    /// 生成（或轮换）某个容器的 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（容器名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌信息；<see cref="ContainerWebhookInfo.Token"/> 是<b>明文，只此一次</b>。</returns>
    /// <exception cref="BusinessException">
    /// 容器名为空（<c>1000</c>）、主机不存在（<c>1300</c>）或主机不可达（<c>1301</c>）。
    /// </exception>
    public async Task<ContainerWebhookInfo> RotateAsync(
        string hostId,
        ContainerWebhookContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerName = RequireContainerName(request.ContainerName);

        var host = await _hosts.FindByNameAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostId}」不存在");

        var token = GenerateToken();
        var hash = HashToken(token);

        var webhook = await _repository
            .FindByHostAndContainerAsync(host.Id, containerName, cancellationToken)
            .ConfigureAwait(false);

        if (webhook is null)
        {
            webhook = ContainerWebhook.Register(Guid.NewGuid(), host.Id, containerName, hash, token[.._tokenHintLength]);
            await _repository.AddAsync(webhook, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            webhook = ContainerWebhook.Register(webhook.Id, host.Id, containerName, hash, token[.._tokenHintLength]);
            await _repository.UpdateAsync(webhook, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "主机 {HostId} 的容器 {ContainerName} 轮换了 Webhook 令牌（提示 {TokenHint}）",
            hostId,
            containerName,
            webhook.WebhookTokenHint);

        return new ContainerWebhookInfo(
            containerName,
            webhook.WebhookEnabled,
            webhook.WebhookTokenHint,
            token,
            webhook.WebhookLastUsedAt);
    }

    /// <summary>
    /// 吊销某个容器的 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（容器名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>吊销后的令牌信息。</returns>
    /// <exception cref="BusinessException">
    /// 容器名为空（<c>1000</c>）或主机不存在（<c>1300</c>）。
    /// </exception>
    public async Task<ContainerWebhookInfo> RevokeAsync(
        string hostId,
        ContainerWebhookContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerName = RequireContainerName(request.ContainerName);

        var host = await _hosts.FindByNameAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostId}」不存在");

        var webhook = await _repository
            .FindByHostAndContainerAsync(host.Id, containerName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BusinessException(
                ErrorCodes.ContainerWebhookNotFound,
                $"容器「{containerName}」还没有配置 Webhook 令牌");

        webhook.Revoke();
        await _repository.UpdateAsync(webhook, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 的容器 {ContainerName} 吊销了 Webhook 令牌", hostId, containerName);

        return new ContainerWebhookInfo(containerName, false, null, null, webhook.WebhookLastUsedAt);
    }

    /// <summary>
    /// 由 Webhook 触发容器重建。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="request">触发请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务受理结果。</returns>
    /// <exception cref="BusinessException">
    /// 令牌无效 / 已吊销（<c>1911</c>）或触发过于频繁（<c>1912</c>）。
    /// </exception>
    public async Task<ResourceTaskAccepted> TriggerAsync(
        string token,
        ContainerWebhookReloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var webhook = await this.RequireWebhookByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        if (!_rateLimiter.TryAcquire(webhook.Id))
        {
            throw new BusinessException(
                ErrorCodes.ContainerWebhookRateLimited,
                "容器 Webhook 触发过于频繁（每分钟最多十次）");
        }

        var host = await _hosts.QueryByIdAsync(webhook.HostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, "容器 Webhook 所属的主机不存在");

        var taskId = GenerateTaskId();

        _tasks.TryRegister(
            taskId,
            host.Name,
            ResourceKinds.Container,
            webhook.ContainerName,
            ContainerOperations.Recreate,
            _timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            webhook.MarkUsed(_timeProvider.GetUtcNow().UtcDateTime);
            await _repository.UpdateAsync(webhook, cancellationToken).ConfigureAwait(false);

            if (!_queue.TryEnqueue(new ResourceOperationRequest(
                taskId,
                host.Name,
                ResourceKinds.Container,
                webhook.ContainerName,
                ContainerOperations.Recreate,
                ComposeContent: null,
                PullAlways: false,
                PruneOrphans: false,
                RemoveVolumes: false,
                ActorId: null,
                ActorName: null,
                ClientIp: null,
                WebhookTokenHint: webhook.WebhookTokenHint,
                VariableOverrides: null,
                Started: request.Start ?? true)))
            {
                throw new BusinessException(
                    ErrorCodes.ComposeDeployFailed,
                    "任务队列已关闭，本次触发未被受理。");
            }
        }
        catch
        {
            _tasks.Abort(taskId);
            throw;
        }

        _logger.LogInformation(
            "容器 {ContainerName}（主机 {HostName}）被 Webhook 触发重建（任务 {TaskId}）",
            webhook.ContainerName,
            host.Name,
            taskId);

        var accepted = new ResourceTaskAccepted(taskId, ResourceTaskStatusText.Queued, 1);

        if (request.Wait == true)
        {
            await this.WaitAsync(taskId, cancellationToken).ConfigureAwait(false);
        }

        return accepted;
    }

    /// <summary>
    /// 由 Webhook 查询任务快照。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务快照；任务不存在或不属于该容器时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">令牌无效（<c>1911</c>）。</exception>
    public async Task<ResourceTaskSnapshot<ContainerRecreateResult>?> GetTaskAsync(
        string token,
        string? taskId,
        CancellationToken cancellationToken = default)
    {
        var webhook = await this.RequireWebhookByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        if (String.IsNullOrWhiteSpace(taskId))
        {
            throw BusinessException.Validation("taskId 不能为空");
        }

        var snapshot = _tasks.Get(taskId);

        if (snapshot is null
            || !String.Equals(snapshot.ResourceName, webhook.ContainerName, StringComparison.Ordinal))
        {
            return null;
        }

        return snapshot;
    }

    /// <summary>
    /// 按令牌反查 Webhook；无效时统一报 <c>1911</c>。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Webhook 实体。</returns>
    private async Task<ContainerWebhook> RequireWebhookByTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(token))
        {
            throw new BusinessException(ErrorCodes.ContainerWebhookTokenInvalid, "Webhook 令牌无效或已吊销");
        }

        var webhook = await _repository
            .FindByTokenHashAsync(HashToken(token), cancellationToken)
            .ConfigureAwait(false);

        return webhook
            ?? throw new BusinessException(ErrorCodes.ContainerWebhookTokenInvalid, "Webhook 令牌无效或已吊销");
    }

    /// <summary>
    /// 等待任务结束。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task WaitAsync(string taskId, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();

        while (_timeProvider.GetElapsedTime(started) < TimeSpan.FromSeconds(_waitTimeoutSeconds))
        {
            var snapshot = _tasks.Get(taskId);

            if (snapshot is not null
                && snapshot.Status is ResourceTaskStatusText.Succeeded or ResourceTaskStatusText.Failed)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_waitPollIntervalMs), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 生成令牌明文。
    /// </summary>
    /// <returns>Base64url 文本。</returns>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(_tokenSizeBytes);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// 计算令牌的 SHA-256 哈希（小写十六进制）。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <returns>哈希文本。</returns>
    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// 生成任务标识。
    /// </summary>
    /// <returns>任务标识。</returns>
    private static string GenerateTaskId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 校验并规范化容器名。
    /// </summary>
    /// <param name="containerName">原始容器名。</param>
    /// <returns>规范化后的容器名（不含前导 <c>/</c>）。</returns>
    /// <exception cref="BusinessException">容器名为空（<c>1000</c>）。</exception>
    private static string RequireContainerName(string? containerName)
    {
        if (String.IsNullOrWhiteSpace(containerName))
        {
            throw BusinessException.Validation("容器名不能为空");
        }

        return containerName.Trim().TrimStart('/');
    }
}

/// <summary>
/// 容器相关的操作类型常量。
/// </summary>
public static class ContainerOperations
{
    /// <summary>
    /// 重建容器。
    /// </summary>
    public const string Recreate = "recreate";
}
