using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Stacks;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排 Webhook 用例：令牌的生成 / 轮换 / 吊销，以及「由令牌触发的重放」。
/// </summary>
/// <remarks>
/// <para><b>Webhook 端点不带 <c>hostId</c></b>：它是<b>匿名</b>端点
/// （凭据就是令牌本身），路径里不能出现主机标识——
/// 那会让 <c>CurrentHostMiddleware</c> 拿路由值去查库，
/// 结果是「令牌还没验证，主机先报错」。主机由令牌反查得到。</para>
/// <para><b>令牌只存哈希</b>：库里是 SHA-256，明文只在生成那一刻返回一次。
/// 比对时同样走哈希反查（<c>FindByWebhookTokenHashAsync</c>），
/// 因此数据库被读走也换不出可用的令牌。</para>
/// <para><b>令牌无效一律 404</b>：不区分「不存在」「已吊销」「未启用」——
/// 区分了就成了令牌探测工具。</para>
/// <para><b>限流是必需的</b>：一次触发就是一次完整部署（含拉镜像）。
/// 没有限流，一个写错的 CI 循环会把守护进程打满。</para>
/// </remarks>
public sealed class StackWebhookService
{
    /// <summary>令牌明文的字节数（32 字节 → Base64url 43 字符）。</summary>
    private const int TokenSizeBytes = 32;

    /// <summary>令牌提示的长度（明文的前若干位）。</summary>
    private const int TokenHintLength = 8;

    /// <summary>限流窗口。</summary>
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    /// <summary>限流窗口内允许的最大触发次数。</summary>
    private const int RateLimitMaxTriggers = 10;

    /// <summary>同步等待任务结束的上限（秒）。</summary>
    /// <remarks>
    /// 反代的读超时（nginx 默认 60s）比它小一点，
    /// 这样「等不到结果」时是我们自己返回 202 + 任务快照，而不是被反代掐成 504。
    /// </remarks>
    private const int WaitTimeoutSeconds = 45;

    /// <summary>同步等待时的轮询间隔（毫秒）。</summary>
    private const int WaitPollIntervalMs = 250;

    private readonly IStackRepository _repository;
    private readonly IHostRepository _hosts;
    private readonly StackTaskRegistry _tasks;
    private readonly StackOperationQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StackWebhookService> _logger;

    /// <summary>各 Stack 的最近触发时刻（用于限流）。</summary>
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _triggers = new();

    /// <summary>
    /// 初始化 Webhook 用例。
    /// </summary>
    /// <param name="repository">Stack 仓储。</param>
    /// <param name="hosts">主机仓储（由主键反查主机名）。</param>
    /// <param name="tasks">任务登记表。</param>
    /// <param name="queue">任务队列。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public StackWebhookService(
        IStackRepository repository,
        IHostRepository hosts,
        StackTaskRegistry tasks,
        StackOperationQueue queue,
        TimeProvider timeProvider,
        ILogger<StackWebhookService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _hosts = hosts;
        _tasks = tasks;
        _queue = queue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 生成（或轮换）Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Stack 名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌信息；<see cref="StackWebhookInfo.Token"/> 是<b>明文，只此一次</b>。</returns>
    /// <exception cref="BusinessException">
    /// 名称为空（<c>1000</c>）、Stack 不存在（<c>1900</c>）、主机不可达（<c>1301</c>）。
    /// </exception>
    public async Task<StackWebhookInfo> RotateAsync(
        string hostId,
        StackWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var stack = await this.RequireStackAsync(hostId, request.Name, cancellationToken)
            .ConfigureAwait(false);

        var token = GenerateToken();
        var hash = HashToken(token);

        stack.SetWebhook(hash, token[..TokenHintLength]);
        await _repository.UpdateAsync(stack, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 的编排 {StackName} 轮换了 Webhook 令牌（提示 {TokenHint}）",
            hostId,
            stack.Name,
            stack.WebhookTokenHint);

        return new StackWebhookInfo(true, stack.WebhookTokenHint, token, stack.WebhookLastUsedAt);
    }

    /// <summary>
    /// 吊销 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Stack 名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>吊销后的令牌信息。</returns>
    /// <exception cref="BusinessException">
    /// 名称为空（<c>1000</c>）、Stack 不存在（<c>1900</c>）。
    /// </exception>
    public async Task<StackWebhookInfo> RevokeAsync(
        string hostId,
        StackWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var stack = await this.RequireStackAsync(hostId, request.Name, cancellationToken)
            .ConfigureAwait(false);

        stack.RevokeWebhook();
        await _repository.UpdateAsync(stack, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 的编排 {StackName} 吊销了 Webhook 令牌", hostId, stack.Name);

        return new StackWebhookInfo(false, null, null, stack.WebhookLastUsedAt);
    }

    /// <summary>
    /// 由 Webhook 触发重放（重新应用 YAML）。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="request">重放请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 不等待时返回任务受理结果；等待时返回任务快照
    /// （若在上限内没跑完，则返回仍在进行中的快照）。
    /// </returns>
    /// <exception cref="BusinessException">
    /// 令牌无效 / 已吊销（<c>1906</c>）、触发过于频繁（<c>1907</c>）
    /// 或已有进行中的操作（<c>1905</c>）。
    /// </exception>
    public async Task<StackTaskAccepted> TriggerAsync(
        string token,
        StackWebhookReloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stack = await this.RequireStackByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        this.EnsureWithinRateLimit(stack.Id);

        var hostName = await this.ResolveHostNameAsync(stack, cancellationToken).ConfigureAwait(false);

        var taskId = GenerateTaskId();

        _tasks.TryRegister(taskId, hostName, stack.Name, StackOperations.Update, _timeProvider.GetUtcNow().UtcDateTime);

        stack.MarkWebhookUsed(_timeProvider.GetUtcNow().UtcDateTime);
        await _repository.UpdateAsync(stack, cancellationToken).ConfigureAwait(false);

        _queue.TryEnqueue(new StackOperationRequest(
            taskId,
            hostName,
            stack.Name,
            StackOperations.Update,
            stack.ComposeContent,
            request.PullAlways ?? false,
            request.PruneOrphans ?? true,
            RemoveVolumes: false,
            ActorId: null,
            ActorName: null,
            ClientIp: null,
            WebhookTokenHint: stack.WebhookTokenHint));

        _logger.LogInformation(
            "编排 {StackName}（主机 {HostName}）被 Webhook 触发重放（任务 {TaskId}）",
            stack.Name,
            hostName,
            taskId);

        var accepted = new StackTaskAccepted(taskId, StackTaskStatusText.Queued, 1);

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
    /// <param name="request">请求（任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务快照；任务不存在或不属于该 Stack 时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">令牌无效（<c>1906</c>）。</exception>
    public async Task<StackTaskSnapshot?> GetTaskAsync(
        string token,
        StackWebhookTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stack = await this.RequireStackByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        var taskId = request.TaskId?.Trim();

        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw BusinessException.Validation("taskId 不能为空");
        }

        var snapshot = _tasks.Get(taskId);

        if (snapshot is null
            || !string.Equals(snapshot.StackName, stack.Name, StringComparison.Ordinal))
        {
            return null;
        }

        return snapshot;
    }

    /// <summary>
    /// 等待任务结束。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task WaitAsync(string taskId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(WaitTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _tasks.Get(taskId);

            if (snapshot is not null
                && snapshot.Status is StackTaskStatusText.Succeeded or StackTaskStatusText.Failed)
            {
                return;
            }

            await Task.Delay(WaitPollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按「主机 + 名称」取 Stack。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="name">Stack 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Stack 实体。</returns>
    private async Task<Stack> RequireStackAsync(
        string hostId,
        string? name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw BusinessException.Validation("Stack 名称不能为空");
        }

        var host = await _hosts.FindByNameAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostId}」不存在");

        var stack = await _repository
            .FindByHostAndNameAsync(host.Id, name.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return stack
            ?? throw new BusinessException(ErrorCodes.StackNotFound, $"编排 Stack「{name.Trim()}」不存在");
    }

    /// <summary>
    /// 按令牌取 Stack。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Stack 实体。</returns>
    /// <exception cref="BusinessException">令牌无效（<c>1906</c>）。</exception>
    private async Task<Stack> RequireStackByTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new BusinessException(ErrorCodes.StackWebhookTokenInvalid, "Webhook 令牌无效");
        }

        var hash = HashToken(token.Trim());

        var stack = await _repository.FindByWebhookTokenHashAsync(hash, cancellationToken).ConfigureAwait(false);

        // 未启用 / 已吊销 / 不存在 —— 一律 1906，不区分
        return stack
            ?? throw new BusinessException(ErrorCodes.StackWebhookTokenInvalid, "Webhook 令牌无效");
    }

    /// <summary>
    /// 由主机主键反查主机名。
    /// </summary>
    /// <param name="stack">Stack 实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机名。</returns>
    private async Task<string> ResolveHostNameAsync(Stack stack, CancellationToken cancellationToken)
    {
        var host = await _hosts.QueryByIdAsync(stack.HostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, "该 Stack 所属的主机已不存在");

        return host.Name;
    }

    /// <summary>
    /// 限流判定。
    /// </summary>
    /// <param name="stackId">Stack 主键。</param>
    /// <exception cref="BusinessException">触发过于频繁（<c>1907</c>）。</exception>
    private void EnsureWithinRateLimit(Guid stackId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var allowed = _triggers.AddOrUpdate(
            stackId,
            _ =>
            {
                var queue = new Queue<DateTime>();
                queue.Enqueue(now);
                return queue;
            },
            (_, existing) =>
            {
                while (existing.Count > 0 && now - existing.Peek() > RateLimitWindow)
                {
                    existing.Dequeue();
                }

                existing.Enqueue(now);
                return existing;
            });

        if (allowed.Count > RateLimitMaxTriggers)
        {
            throw new BusinessException(
                ErrorCodes.StackWebhookRateLimited,
                $"Webhook 触发过于频繁（{RateLimitWindow.TotalSeconds:F0} 秒内最多 {RateLimitMaxTriggers} 次）");
        }
    }

    /// <summary>
    /// 生成令牌明文。
    /// </summary>
    /// <returns>Base64url 文本。</returns>
    private static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenSizeBytes));

    /// <summary>
    /// 计算令牌哈希。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <returns>小写十六进制 SHA-256。</returns>
    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 生成任务标识。
    /// </summary>
    /// <returns>Base64url 文本。</returns>
    private static string GenerateTaskId() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenSizeBytes));

    /// <summary>
    /// Base64url 编码（无填充）。
    /// </summary>
    /// <param name="payload">字节。</param>
    /// <returns>Base64url 文本。</returns>
    private static string Base64UrlEncode(byte[] payload) =>
        Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
