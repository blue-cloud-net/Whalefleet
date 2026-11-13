using System.Net;

using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Alerting;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;

using Whalefleet.Core.Alerting;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Images;

namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像自动更新：策略管理 + 检查 + 自动应用。
/// </summary>
/// <remarks>
/// <para><b>检查只问摘要，不拉镜像</b>：<see cref="IRegistryManifestProbe"/> 发一个
/// <c>HEAD</c> 就够。走 <c>docker pull</c> 检查更新会产生几百 MB 流量。</para>
/// <para><b>首次检查只建立基线</b>：见 <see cref="ImageUpdatePolicy.RecordProbe"/>——
/// 刚加上的策略不该立刻宣称「有更新」，否则用户第一次看到的就是一屏红点。</para>
/// <para><b>自动应用 = 拉取 + 按原配置重建</b>：重建走
/// <see cref="ContainerService.RecreateByWebhookAsync"/> 的同一条内核
/// （从 inspect 还原完整配置、失败自动回滚）。<b>不在这里另写一套创建逻辑</b>——
/// 那套配置有 capabilities / sysctls / ulimits / 日志驱动等几十个字段，
/// 漏一个就是静默丢配置。</para>
/// <para><b>失败一律进通知中心，不写告警规则</b>：告警规则是用户自己配的
/// 「什么条件下通知我」，而「自动更新失败了」是系统对自身动作的报告——
/// 塞进告警规则等于替用户配了一条他没配过的规则。</para>
/// </remarks>
public sealed class ImageUpdateService
{
    private readonly IImageUpdatePolicyRepository _policies;
    private readonly IHostRepository _hosts;
    private readonly IDockerHostAccessor _accessor;
    private readonly IRegistryManifestProbe _probe;
    private readonly ImageService _images;
    private readonly ContainerService _containers;
    private readonly INotificationWriter _notifications;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ImageUpdateService> _logger;

    /// <summary>
    /// 初始化用例。
    /// </summary>
    /// <param name="policies">策略仓储。</param>
    /// <param name="hosts">主机仓储。</param>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="probe">仓库清单探测器。</param>
    /// <param name="images">镜像用例（拉取）。</param>
    /// <param name="containers">容器用例（重建）。</param>
    /// <param name="notifications">通知写入。</param>
    /// <param name="audit">审计写入。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ImageUpdateService(
        IImageUpdatePolicyRepository policies,
        IHostRepository hosts,
        IDockerHostAccessor accessor,
        IRegistryManifestProbe probe,
        ImageService images,
        ContainerService containers,
        INotificationWriter notifications,
        IAuditLogger audit,
        TimeProvider clock,
        ILogger<ImageUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _policies = policies;
        _hosts = hosts;
        _accessor = accessor;
        _probe = probe;
        _images = images;
        _containers = containers;
        _notifications = notifications;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// 列出某台主机的全部策略。
    /// </summary>
    /// <param name="hostName">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>策略列表（按镜像引用升序）。</returns>
    /// <exception cref="BusinessException">主机不存在（<c>1300</c>）。</exception>
    public async Task<IReadOnlyList<ImageUpdatePolicyDto>> ListAsync(
        string hostName,
        CancellationToken cancellationToken = default)
    {
        var host = await _accessor.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);
        var policies = await _policies.ListByHostAsync(host.Id, cancellationToken).ConfigureAwait(false);

        return [.. policies.Select(ToDto)];
    }

    /// <summary>
    /// 新增一条策略。
    /// </summary>
    /// <param name="hostName">主机标识（主机名）。</param>
    /// <param name="request">请求（镜像引用、模式、是否启用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的策略。</returns>
    /// <exception cref="BusinessException">
    /// 镜像引用为空或非法（<c>1000</c>）、模式非法（<c>1000</c>）、
    /// 该主机上已有同一镜像的策略（<c>1412</c>）或主机不存在（<c>1300</c>）。
    /// </exception>
    public async Task<ImageUpdatePolicyDto> CreateAsync(
        string hostName,
        CreateImageUpdatePolicyRequest? request,
        CancellationToken cancellationToken = default)
    {
        var host = await _accessor.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);

        if (String.IsNullOrWhiteSpace(request?.Reference))
        {
            throw BusinessException.Validation("reference 不能为空");
        }

        ImageUpdatePolicy policy;

        try
        {
            policy = ImageUpdatePolicy.Create(
                Guid.NewGuid(),
                host.Id,
                request.Reference,
                ParseMode(request.Mode),
                request.Enabled ?? true);
        }
        catch (ArgumentException error)
        {
            throw BusinessException.Validation(error.Message);
        }

        var existing = await _policies
            .FindAsync(host.Id, policy.ImageReference, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            throw new BusinessException(
                ErrorCodes.ImageUpdatePolicyAlreadyExists,
                $"主机「{host.Name}」上已有镜像「{policy.ImageReference}」的更新策略");
        }

        await _policies.AddAsync(policy, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(
            AuditActions.ImageUpdatePolicyChange,
            policy,
            host.Name,
            succeeded: true,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostName} 新增镜像更新策略 {Reference}（模式 {Mode}）",
            host.Name,
            policy.ImageReference,
            policy.Mode);

        return ToDto(policy);
    }

    /// <summary>
    /// 修改策略的执行模式与启用状态。
    /// </summary>
    /// <param name="hostName">主机标识（主机名）。</param>
    /// <param name="request">请求（策略主键、模式、是否启用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>修改后的策略。</returns>
    /// <exception cref="BusinessException">
    /// 策略不存在（<c>1411</c>）、模式非法（<c>1000</c>）或主机不存在（<c>1300</c>）。
    /// </exception>
    public async Task<ImageUpdatePolicyDto> UpdateAsync(
        string hostName,
        UpdateImageUpdatePolicyRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (host, policy) = await this.LoadAsync(hostName, request.Id, cancellationToken)
            .ConfigureAwait(false);

        if (!String.IsNullOrWhiteSpace(request.Mode))
        {
            policy.SetMode(ParseMode(request.Mode));
        }

        if (request.Enabled is { } enabled)
        {
            policy.SetEnabled(enabled);
        }

        await _policies.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(
            AuditActions.ImageUpdatePolicyChange,
            policy,
            host.Name,
            succeeded: true,
            cancellationToken).ConfigureAwait(false);

        return ToDto(policy);
    }

    /// <summary>
    /// 删除策略。
    /// </summary>
    /// <param name="hostName">主机标识（主机名）。</param>
    /// <param name="request">请求（策略主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">策略不存在（<c>1411</c>）或主机不存在（<c>1300</c>）。</exception>
    public async Task RemoveAsync(
        string hostName,
        RemoveImageUpdatePolicyRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (host, policy) = await this.LoadAsync(hostName, request.Id, cancellationToken)
            .ConfigureAwait(false);

        await _policies.RemoveByIdAsync(policy.Id, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(
            AuditActions.ImageUpdatePolicyChange,
            policy,
            host.Name,
            succeeded: true,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostName} 删除镜像更新策略 {Reference}",
            host.Name,
            policy.ImageReference);
    }

    /// <summary>
    /// 立即检查一条策略（自动模式下会顺带应用）。
    /// </summary>
    /// <param name="hostName">主机标识（主机名）。</param>
    /// <param name="request">请求（策略主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>检查后的策略与本次重建的容器数。</returns>
    /// <exception cref="BusinessException">策略不存在（<c>1411</c>）或主机不存在（<c>1300</c>）。</exception>
    public async Task<ImageUpdateCheckResult> CheckAsync(
        string hostName,
        CheckImageUpdatePolicyRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (host, policy) = await this.LoadAsync(hostName, request.Id, cancellationToken)
            .ConfigureAwait(false);

        var recreated = await this.CheckCoreAsync(policy, host.Name, cancellationToken)
            .ConfigureAwait(false);

        return new ImageUpdateCheckResult(ToDto(policy), recreated);
    }

    /// <summary>
    /// 扫描全部启用的策略（供后台工作器调用）。
    /// </summary>
    /// <param name="maxPolicies">单轮上限；<see langword="null"/> 表示不限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本轮汇总。</returns>
    /// <remarks>
    /// <b>单条失败不中断整轮</b>：一台主机的网络问题不该让其它主机的策略一起停摆。
    /// </remarks>
    public async Task<ImageUpdateScanSummary> ScanAsync(
        int? maxPolicies,
        CancellationToken cancellationToken = default)
    {
        var policies = await _policies.ListEnabledAsync(cancellationToken).ConfigureAwait(false);

        var checkedCount = 0;
        var updated = 0;
        var applied = 0;
        var failed = 0;

        foreach (var policy in policies)
        {
            if (maxPolicies is { } limit && checkedCount >= limit)
            {
                _logger.LogDebug("本轮镜像更新扫描已达上限 {Limit}，剩余策略下一轮再查", limit);
                break;
            }

            var host = await _hosts.QueryByIdAsync(policy.HostId, cancellationToken).ConfigureAwait(false);

            if (host is null)
            {
                // 主机已被删除：策略留着也查不了，直接跳过（不做级联删除——
                // 主机可能只是暂时被移出管理，而历史结论仍有参考价值）。
                continue;
            }

            checkedCount++;

            try
            {
                var recreated = await this.CheckCoreAsync(policy, host.Name, cancellationToken)
                    .ConfigureAwait(false);

                if (policy.State == ImageUpdateState.UpdateAvailable)
                {
                    updated++;
                }
                else if (policy.State == ImageUpdateState.Failed)
                {
                    failed++;
                }
                else if (recreated > 0)
                {
                    applied++;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed++;

                _logger.LogWarning(
                    error,
                    "检查镜像 {Reference}（主机 {HostName}）时出错",
                    policy.ImageReference,
                    host.Name);
            }
        }

        return new ImageUpdateScanSummary(checkedCount, updated, applied, failed);
    }

    /// <summary>
    /// 检查一条策略，必要时应用。
    /// </summary>
    /// <param name="policy">策略实体（会被就地更新并落库）。</param>
    /// <param name="hostName">主机名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次重建的容器数。</returns>
    private async Task<int> CheckCoreAsync(
        ImageUpdatePolicy policy,
        string hostName,
        CancellationToken cancellationToken)
    {
        var (repository, tag) = ImageReference.Split(policy.ImageReference);

        var probe = await _probe.ProbeAsync(repository, tag, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (!probe.Succeeded)
        {
            policy.RecordFailure(now, probe.Message ?? "无法获取仓库摘要");
            await _policies.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);

            await this.NotifyAsync(
                NotificationSourceKind.ImageUpdateFailed,
                AlertSeverity.Warning,
                "notification.imageUpdate.failed",
                policy,
                hostName,
                probe.Message ?? "无法获取仓库摘要",
                now,
                cancellationToken).ConfigureAwait(false);

            return 0;
        }

        var localDigest = await this.ResolveLocalDigestAsync(hostName, policy.ImageReference, cancellationToken)
            .ConfigureAwait(false);

        policy.RecordProbe(now, probe.Digest!, localDigest);
        await _policies.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);

        if (policy.State != ImageUpdateState.UpdateAvailable)
        {
            return 0;
        }

        if (policy.Mode == ImageUpdateMode.Auto)
        {
            return await this.ApplyAsync(policy, hostName, cancellationToken).ConfigureAwait(false);
        }

        if (policy.ShouldNotify(probe.Digest!))
        {
            await this.NotifyAsync(
                NotificationSourceKind.ImageUpdateAvailable,
                AlertSeverity.Info,
                "notification.imageUpdate.available",
                policy,
                hostName,
                probe.Digest!,
                now,
                cancellationToken).ConfigureAwait(false);

            policy.MarkNotified(probe.Digest!);
            await _policies.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// 拉取新版本并重建引用该镜像的容器。
    /// </summary>
    /// <param name="policy">策略实体。</param>
    /// <param name="hostName">主机名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重建的容器数。</returns>
    /// <remarks>
    /// <para><b>先拉再重建</b>：顺序反了会重建出一批还引用旧层的容器，
    /// 而「更新完成」的结论就变成了假的。</para>
    /// <para><b>重建失败要能被看见</b>：写通知 + 审计，并把策略标成失败。
    /// 静默吞掉的话，用户以为自动更新工作正常，实际跑的还是旧版本。</para>
    /// </remarks>
    private async Task<int> ApplyAsync(
        ImageUpdatePolicy policy,
        string hostName,
        CancellationToken cancellationToken)
    {
        var (repository, tag) = ImageReference.Split(policy.ImageReference);
        var now = _clock.GetUtcNow().UtcDateTime;

        try
        {
            await _images.PullAsync(
                hostName,
                new PullImageRequest { Image = repository, Tag = tag },
                cancellationToken).ConfigureAwait(false);

            var client = await _accessor.GetClientAsync(hostName, cancellationToken).ConfigureAwait(false);

            var containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                cancellationToken).ConfigureAwait(false);

            var targets = containers
                .Where(container =>
                    String.Equals(container.Image, policy.ImageReference, StringComparison.Ordinal)
                    || String.Equals(container.Image, $"{repository}:{tag}", StringComparison.Ordinal))
                .ToList();

            foreach (var target in targets)
            {
                await _containers
                    .RecreateByWebhookAsync(hostName, target.ID, start: null, cancellationToken)
                    .ConfigureAwait(false);
            }

            policy.RecordApplied(now, policy.RemoteDigest!, $"重建了 {targets.Count} 个容器");
            await _policies.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);

            await this.RecordAuditAsync(
                AuditActions.ImageUpdateApply,
                policy,
                hostName,
                succeeded: true,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "主机 {HostName} 的镜像 {Reference} 已自动更新（重建 {Count} 个容器）",
                hostName,
                policy.ImageReference,
                targets.Count);

            return targets.Count;
        }
        catch (BusinessException error)
        {
            policy.RecordFailure(now, error.Message);
            await _policies.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);

            await this.NotifyAsync(
                NotificationSourceKind.ImageUpdateFailed,
                AlertSeverity.Critical,
                "notification.imageUpdate.failed",
                policy,
                hostName,
                error.Message,
                now,
                cancellationToken).ConfigureAwait(false);

            await this.RecordAuditAsync(
                AuditActions.ImageUpdateApply,
                policy,
                hostName,
                succeeded: false,
                cancellationToken).ConfigureAwait(false);

            return 0;
        }
    }

    /// <summary>
    /// 读出本地已缓存镜像的摘要。
    /// </summary>
    /// <param name="hostName">主机名。</param>
    /// <param name="reference">镜像引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本地摘要；本地没有该镜像时返回 <see langword="null"/>。</returns>
    private async Task<string?> ResolveLocalDigestAsync(
        string hostName,
        string reference,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await _accessor.GetClientAsync(hostName, cancellationToken).ConfigureAwait(false);
            var image = await client.Images.InspectImageAsync(reference, cancellationToken).ConfigureAwait(false);

            return image.RepoDigests?
                .Select(digest => digest.Split('@', 2).LastOrDefault())
                .FirstOrDefault(digest => !String.IsNullOrWhiteSpace(digest));
        }
        catch (DockerApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // 本地没有这个镜像：正常情况（策略可以先配、镜像还没拉）。
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "读取主机 {HostName} 的本地镜像 {Reference} 失败", hostName, reference);

            return null;
        }
    }

    /// <summary>
    /// 写一条站内通知。
    /// </summary>
    private async Task NotifyAsync(
        NotificationSourceKind kind,
        AlertSeverity severity,
        string messageKey,
        ImageUpdatePolicy policy,
        string hostName,
        string detail,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.CreateAsync(
                new NotificationDraft(
                    kind,
                    severity,
                    messageKey,
                    new Dictionary<string, object?>
                    {
                        ["image"] = policy.ImageReference,
                        ["host"] = hostName,
                        ["detail"] = detail,
                    },
                    policy.HostId,
                    hostName,
                    // 去重键带摘要：同一版只提醒一次，但新版仍会提醒。
                    DedupeKey: $"image-update:{policy.Id:N}:{policy.RemoteDigest ?? "unknown"}",
                    occurredAt),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "写镜像更新通知失败（策略 {PolicyId}）", policy.Id);
        }
    }

    /// <summary>
    /// 写一条审计。
    /// </summary>
    private async Task RecordAuditAsync(
        string action,
        ImageUpdatePolicy policy,
        string hostName,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audit.RecordAsync(
                new AuditLogDraft
                {
                    Category = AuditCategory.Activity,
                    Action = action,
                    Outcome = succeeded ? AuditOutcome.Success : AuditOutcome.Failure,
                    OccurredAt = _clock.GetUtcNow().UtcDateTime,
                    TargetType = AuditTargetTypes.Image,
                    TargetId = policy.Id.ToString("D"),
                    TargetName = policy.ImageReference,
                    HostName = hostName,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "写镜像更新审计失败（策略 {PolicyId}）", policy.Id);
        }
    }

    /// <summary>
    /// 按「主机 + 策略主键」取出策略并校验归属。
    /// </summary>
    private async Task<(DockerHost Host, ImageUpdatePolicy Policy)> LoadAsync(
        string hostName,
        Guid policyId,
        CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);

        if (policyId == Guid.Empty)
        {
            throw BusinessException.Validation("id 不能为空");
        }

        var policy = await _policies.QueryByIdAsync(policyId, cancellationToken).ConfigureAwait(false);

        // 策略存在但属于别的主机时也报「不存在」：
        // 报「属于别的主机」等于泄露了「这个 ID 存在」。
        if (policy is null || policy.HostId != host.Id)
        {
            throw new BusinessException(
                ErrorCodes.ImageUpdatePolicyNotFound,
                "镜像更新策略不存在");
        }

        return (host, policy);
    }

    /// <summary>
    /// 解析执行模式文本。
    /// </summary>
    /// <param name="text">文本（<c>notify</c> / <c>auto</c>）；为空时取
    /// <c>notify</c>。</param>
    /// <returns>执行模式。</returns>
    /// <exception cref="BusinessException">文本非法（<c>1000</c>）。</exception>
    private static ImageUpdateMode ParseMode(string? text)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return ImageUpdateMode.Notify;
        }

        try
        {
            return ImageUpdateModeText.Parse(text);
        }
        catch (FormatException)
        {
            throw BusinessException.Validation("mode 只能是 notify 或 auto");
        }
    }

    /// <summary>
    /// 实体 → 接口投影。
    /// </summary>
    private static ImageUpdatePolicyDto ToDto(ImageUpdatePolicy policy) =>
        new(
            policy.Id,
            policy.ImageReference,
            ImageUpdateModeText.ToText(policy.Mode),
            policy.Enabled,
            ImageUpdateStateText.ToText(policy.State),
            policy.BaselineDigest,
            policy.RemoteDigest,
            policy.LastMessage,
            policy.LastCheckedAt,
            policy.LastAppliedAt);
}
