using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Core.Auditing;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Auditing;

/// <summary>
/// 审计日志写入的默认实现：补齐默认值、落库，失败只记日志。
/// </summary>
/// <remarks>
/// <para><b>默认值从哪来</b>：操作者（<see cref="ICurrentUser"/>）、来源 IP
/// （<see cref="ICurrentClientIp"/>）、时间（<see cref="TimeProvider"/>）。
/// 调用方只在「自己更清楚」时才覆盖它们——典型场景是登录失败：
/// 那时请求是匿名的，操作者名要取**尝试的用户名**而不是当前用户。</para>
/// <para><b>为什么吞异常</b>：见 <see cref="IAuditLogger"/> 的 remarks。
/// 这里把失败记成 Error 并带上动作标识，运维据此能知道「审计链路坏了」。</para>
/// </remarks>
public sealed class AuditLogger : IAuditLogger
{
    private readonly IAuditLogRepository _repository;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentClientIp _clientIp;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditLogger> _logger;

    /// <summary>
    /// 初始化审计写入器。
    /// </summary>
    /// <param name="repository">审计仓储。</param>
    /// <param name="currentUser">当前登录用户（操作者默认值）。</param>
    /// <param name="clientIp">来源 IP（默认值）。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AuditLogger(
        IAuditLogRepository repository,
        ICurrentUser currentUser,
        ICurrentClientIp clientIp,
        TimeProvider timeProvider,
        ILogger<AuditLogger> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _currentUser = currentUser;
        _clientIp = clientIp;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditLogDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            var completed = draft with
            {
                ActorId = draft.ActorId ?? _currentUser.UserId,
                ActorName = String.IsNullOrWhiteSpace(draft.ActorName)
                    ? _currentUser.Username ?? AuditLog.AnonymousActor
                    : draft.ActorName,
                ClientIp = draft.ClientIp ?? _clientIp.Ip,
                OccurredAt = draft.OccurredAt == default ? now : draft.OccurredAt,
            };

            var log = AuditLog.Record(Guid.NewGuid(), completed);

            // 刻意不用调用方的取消令牌：请求被中止时审计更需要落库。
            await _repository.AddAsync(log, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // best-effort：审计写不进去不能让业务失败，但必须留下「链路坏了」的信号。
            _logger.LogError(
                exception,
                "审计写入失败（动作={Action}，类别={Category}，结果={Outcome}）",
                draft.Action,
                draft.Category,
                draft.Outcome);
        }
    }
}
