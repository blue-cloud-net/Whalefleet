using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Operations;
using Whalefleet.Application.Variables;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Composes;

namespace Whalefleet.Application.Composes;

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
public sealed class ComposeWebhookService
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

    private readonly IComposeProjectRepository _repository;
    private readonly IHostRepository _hosts;
    private readonly EnvVariableService _variables;
    private readonly ResourceTaskRegistry<ComposeOperationResult> _tasks;
    private readonly ComposeOperationQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ComposeWebhookService> _logger;

    private readonly ComposeWebhookRateLimiter _rateLimiter;

    /// <summary>
    /// 初始化 Webhook 用例。
    /// </summary>
    /// <param name="repository">Compose 项目仓储。</param>
    /// <param name="hosts">主机仓储（由主键反查主机名）。</param>
    /// <param name="variables">环境变量用例（校验 Webhook 的变量覆盖白名单）。</param>
    /// <param name="tasks">任务登记表。</param>
    /// <param name="queue">任务队列。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <param name="rateLimiter">跨请求共享的限流器。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ComposeWebhookService(
        IComposeProjectRepository repository,
        IHostRepository hosts,
        EnvVariableService variables,
        ResourceTaskRegistry<ComposeOperationResult> tasks,
        ComposeOperationQueue queue,
        TimeProvider timeProvider,
        ILogger<ComposeWebhookService> logger,
        ComposeWebhookRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        ArgumentNullException.ThrowIfNull(rateLimiter);
        _rateLimiter = rateLimiter;
        _repository = repository;
        _hosts = hosts;
        _variables = variables;
        _tasks = tasks;
        _queue = queue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 生成（或轮换）Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Compose 项目名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌信息；<see cref="ComposeWebhookInfo.Token"/> 是<b>明文，只此一次</b>。</returns>
    /// <exception cref="BusinessException">
    /// 名称为空（<c>1000</c>）、Compose 项目不存在（<c>1900</c>）、主机不可达（<c>1301</c>）。
    /// </exception>
    public async Task<ComposeWebhookInfo> RotateAsync(
        string hostId,
        ComposeWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var compose = await this.RequireComposeAsync(hostId, request.Name, cancellationToken)
            .ConfigureAwait(false);

        var token = GenerateToken();
        var hash = HashToken(token);

        compose.SetWebhook(hash, token[.._tokenHintLength]);
        await _repository.UpdateAsync(compose, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 的编排 {ComposeName} 轮换了 Webhook 令牌（提示 {TokenHint}）",
            hostId,
            compose.Name,
            compose.WebhookTokenHint);

        return new ComposeWebhookInfo(true, compose.WebhookTokenHint, token, compose.WebhookLastUsedAt);
    }

    /// <summary>
    /// 吊销 Webhook 令牌。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Compose 项目名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>吊销后的令牌信息。</returns>
    /// <exception cref="BusinessException">
    /// 名称为空（<c>1000</c>）、Compose 项目不存在（<c>1900</c>）。
    /// </exception>
    public async Task<ComposeWebhookInfo> RevokeAsync(
        string hostId,
        ComposeWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var compose = await this.RequireComposeAsync(hostId, request.Name, cancellationToken)
            .ConfigureAwait(false);

        compose.RevokeWebhook();
        await _repository.UpdateAsync(compose, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 的编排 {ComposeName} 吊销了 Webhook 令牌", hostId, compose.Name);

        return new ComposeWebhookInfo(false, null, null, compose.WebhookLastUsedAt);
    }

    /// <summary>
    /// 由 Webhook 触发重放（重新应用 YAML）。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="request">重放请求。</param>
    /// <param name="overrides">
    /// 本次的变量覆盖（通常来自 URL 的查询串）。
    /// 可为空；<b>非白名单的键会被忽略</b>，而覆盖密钥会报错（<c>1910</c>）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 不等待时返回任务受理结果；等待时返回任务快照
    /// （若在上限内没跑完，则返回仍在进行中的快照）。
    /// </returns>
    /// <exception cref="BusinessException">
    /// 令牌无效 / 已吊销（<c>1906</c>）、触发过于频繁（<c>1907</c>）、
    /// 已有进行中的操作（<c>1905</c>）或试图覆盖密钥（<c>1910</c>）。
    /// </exception>
    public async Task<ComposeTaskAccepted> TriggerAsync(
        string token,
        ComposeWebhookReloadRequest request,
        IReadOnlyDictionary<string, string>? overrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var compose = await this.RequireComposeByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        this.EnsureWithinRateLimit(compose.Id);

        var hostName = await this.ResolveHostNameAsync(compose, cancellationToken).ConfigureAwait(false);

        // 覆盖项先过一遍白名单：未声明 / 未勾选可覆盖的键被忽略，
        // 而覆盖密钥直接报错（静默忽略会让 CI 以为口令已经改了）。
        var resolved = await _variables
            .ResolveAsync(compose.HostId, compose.Name, overrides, cancellationToken)
            .ConfigureAwait(false);

        var taskId = GenerateTaskId();

        _tasks.TryRegister(taskId, hostName, ResourceKinds.Compose, compose.Name, ComposeOperations.Update, _timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            compose.MarkWebhookUsed(_timeProvider.GetUtcNow().UtcDateTime);
            if (!await _repository.UpdateAsync(compose, cancellationToken).ConfigureAwait(false))
            {
                throw new BusinessException(ErrorCodes.ComposeNotFound, "编排 Compose 项目已不存在。");
            }

            if (!_queue.TryEnqueue(new ComposeOperationRequest(
            taskId,
            hostName,
            compose.Name,
            ComposeOperations.Update,
            compose.ComposeContent,
            request.PullAlways ?? false,
            request.PruneOrphans ?? true,
            RemoveVolumes: false,
            ActorId: null,
            ActorName: null,
            ClientIp: null,
            WebhookTokenHint: compose.WebhookTokenHint,
            VariableOverrides: overrides)))
            {
                throw new BusinessException(ErrorCodes.ComposeDeployFailed, "编排队列已关闭，本次触发未被受理。");
            }
        }
        catch
        {
            _tasks.Abort(taskId);
            throw;
        }

        _logger.LogInformation(
            "编排 {ComposeName}（主机 {HostName}）被 Webhook 触发重放（任务 {TaskId}，生效覆盖 {Applied}，忽略 {Ignored}）",
            compose.Name,
            hostName,
            taskId,
            resolved.AppliedOverrides.Count,
            resolved.IgnoredOverrides.Count);

        var accepted = new ComposeTaskAccepted(taskId, ResourceTaskStatusText.Queued, 1)
        {
            AppliedVariables = resolved.AppliedOverrides,
            IgnoredVariables = resolved.IgnoredOverrides,
        };

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
    /// <returns>任务快照；任务不存在或不属于该 Compose 项目时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">令牌无效（<c>1906</c>）。</exception>
    public async Task<ResourceTaskSnapshot<ComposeOperationResult>?> GetTaskAsync(
        string token,
        ComposeWebhookTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var compose = await this.RequireComposeByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        var taskId = request.TaskId?.Trim();

        if (String.IsNullOrWhiteSpace(taskId))
        {
            throw BusinessException.Validation("taskId 不能为空");
        }

        var snapshot = _tasks.Get(taskId);

        if (snapshot is null
            || !String.Equals(snapshot.ResourceName, compose.Name, StringComparison.Ordinal))
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
    /// 按「主机 + 名称」取 Compose 项目。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="name">Compose 项目名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Compose 项目实体。</returns>
    private async Task<ComposeProject> RequireComposeAsync(
        string hostId,
        string? name,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(name))
        {
            throw BusinessException.Validation("Compose 项目名称不能为空");
        }

        var host = await _hosts.FindByNameAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostId}」不存在");

        var compose = await _repository
            .FindByHostAndNameAsync(host.Id, name.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return compose
            ?? throw new BusinessException(ErrorCodes.ComposeNotFound, $"编排 Compose 项目「{name.Trim()}」不存在");
    }

    /// <summary>
    /// 按令牌取 Compose 项目。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Compose 项目实体。</returns>
    /// <exception cref="BusinessException">令牌无效（<c>1906</c>）。</exception>
    private async Task<ComposeProject> RequireComposeByTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(token))
        {
            throw new BusinessException(ErrorCodes.ComposeWebhookTokenInvalid, "Webhook 令牌无效");
        }

        var hash = HashToken(token.Trim());

        var compose = await _repository.FindByWebhookTokenHashAsync(hash, cancellationToken).ConfigureAwait(false);

        // 未启用 / 已吊销 / 不存在 —— 一律 1906，不区分
        return compose
            ?? throw new BusinessException(ErrorCodes.ComposeWebhookTokenInvalid, "Webhook 令牌无效");
    }

    /// <summary>
    /// 由主机主键反查主机名。
    /// </summary>
    /// <param name="compose">Compose 项目实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机名。</returns>
    private async Task<string> ResolveHostNameAsync(ComposeProject compose, CancellationToken cancellationToken)
    {
        var host = await _hosts.QueryByIdAsync(compose.HostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, "该 Compose 项目所属的主机已不存在");

        return host.Name;
    }

    /// <summary>
    /// 限流判定。
    /// </summary>
    /// <param name="composeId">Compose 项目主键。</param>
    /// <exception cref="BusinessException">触发过于频繁（<c>1907</c>）。</exception>
    private void EnsureWithinRateLimit(Guid composeId)
    {
        if (!_rateLimiter.TryAcquire(composeId))
        {
            throw new BusinessException(ErrorCodes.ComposeWebhookRateLimited,
                "Webhook 触发过于频繁（60 秒内最多 10 次）。");
        }
    }

    /// <summary>
    /// 生成令牌明文。
    /// </summary>
    /// <returns>Base64url 文本。</returns>
    private static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(_tokenSizeBytes));

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
        Base64UrlEncode(RandomNumberGenerator.GetBytes(_tokenSizeBytes));

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
