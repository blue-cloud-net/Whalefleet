using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 告警规则用例。
/// </summary>
public sealed class AlertRuleService
{
    private readonly IAlertRuleRepository _rules;
    private readonly IAlertRuleChannelRepository _ruleChannels;
    private readonly IHostRepository _hosts;
    private readonly IAuditLogger _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    public AlertRuleService(
        IAlertRuleRepository rules,
        IAlertRuleChannelRepository ruleChannels,
        IHostRepository hosts,
        IAuditLogger audit,
        ICurrentUser currentUser,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(ruleChannels);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(clock);

        _rules = rules;
        _ruleChannels = ruleChannels;
        _hosts = hosts;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <summary>
    /// 列出规则（某台主机的可见规则：全局 + 该主机）。
    /// </summary>
    /// <param name="host">主机名；空表示只看全局规则。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<AlertRuleDto>> ListAsync(
        string? host,
        CancellationToken cancellationToken = default)
    {
        var (hostId, _) = await this.ResolveHostAsync(host, cancellationToken).ConfigureAwait(false);
        var rules = await _rules.ListForHostAsync(hostId, cancellationToken).ConfigureAwait(false);
        var hosts = await _hosts.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
        var hostNames = hosts.ToDictionary(host => host.Id, host => host.Name);

        var channelMap = await _ruleChannels
            .GetChannelIdsAsync([.. rules.Select(rule => rule.Id)], cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rules.Select(rule => ToDto(
                rule,
                rule.HostId is { } id && hostNames.TryGetValue(id, out var name) ? name : null,
                channelMap.TryGetValue(rule.Id, out var channelIds) ? channelIds : [])),
        ];
    }

    /// <summary>
    /// 新建规则。
    /// </summary>
    public async Task<AlertRuleDto> CreateAsync(
        CreateAlertRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (hostId, hostName) = await this.ResolveHostAsync(request.Host, cancellationToken)
            .ConfigureAwait(false);

        var kind = ParseKind(request.Kind);

        if (await _rules.ExistsAsync(hostId, kind, excludeId: null, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.AlertRuleInvalid,
                $"该作用域下已存在 {AlertKindText.ToText(kind)} 规则");
        }

        AlertRule rule;

        try
        {
            rule = AlertRule.Create(
                Guid.NewGuid(),
                hostId,
                kind,
                request.Enabled,
                request.InfoThreshold,
                request.WarningThreshold,
                request.CriticalThreshold,
                request.Severity,
                request.ConsecutiveSamples,
                request.WindowSeconds,
                request.CooldownSeconds);
        }
        catch (ArgumentException exception)
        {
            throw new BusinessException(ErrorCodes.AlertRuleInvalid, exception.Message, exception);
        }

        await _rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);

        await _ruleChannels.ReplaceAsync(
            rule.Id,
            request.ChannelIds ?? [],
            _currentUser.UserId ?? Guid.Empty,
            _clock.GetUtcNow().UtcDateTime,
            cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(rule, hostName, "创建", cancellationToken).ConfigureAwait(false);

        return ToDto(rule, hostName, request.ChannelIds ?? []);
    }

    /// <summary>
    /// 更新规则。
    /// </summary>
    public async Task<AlertRuleDto> UpdateAsync(
        UpdateAlertRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await _rules.QueryByIdAsync(request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.AlertRuleNotFound, "告警规则不存在");

        var (hostId, hostName) = await this.ResolveHostAsync(request.Host, cancellationToken)
            .ConfigureAwait(false);

        var kind = ParseKind(request.Kind);

        if (await _rules.ExistsAsync(hostId, kind, request.Id, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.AlertRuleInvalid,
                $"该作用域下已存在 {AlertKindText.ToText(kind)} 规则");
        }

        try
        {
            rule.Update(
                hostId,
                kind,
                request.Enabled,
                request.InfoThreshold,
                request.WarningThreshold,
                request.CriticalThreshold,
                request.Severity,
                request.ConsecutiveSamples,
                request.WindowSeconds,
                request.CooldownSeconds);
        }
        catch (ArgumentException exception)
        {
            throw new BusinessException(ErrorCodes.AlertRuleInvalid, exception.Message, exception);
        }

        if (!await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.AlertRuleNotFound, "告警规则不存在");
        }

        await _ruleChannels.ReplaceAsync(
            rule.Id,
            request.ChannelIds ?? [],
            _currentUser.UserId ?? Guid.Empty,
            _clock.GetUtcNow().UtcDateTime,
            cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(rule, hostName, "更新", cancellationToken).ConfigureAwait(false);

        return ToDto(rule, hostName, request.ChannelIds ?? []);
    }

    /// <summary>
    /// 启用 / 停用规则。
    /// </summary>
    public async Task SetEnabledAsync(
        SetAlertRuleEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await _rules.QueryByIdAsync(request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.AlertRuleNotFound, "告警规则不存在");

        rule.SetEnabled(request.Enabled);

        if (!await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.AlertRuleNotFound, "告警规则不存在");
        }

        await this.RecordAuditAsync(rule, null, request.Enabled ? "启用" : "停用", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 删除规则（软删除）。
    /// </summary>
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await _rules.QueryByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.AlertRuleNotFound, "告警规则不存在");

        if (!await _rules.RemoveByIdAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(ErrorCodes.AlertRuleNotFound, "告警规则不存在");
        }

        await _ruleChannels.ReplaceAsync(
            id,
            [],
            _currentUser.UserId ?? Guid.Empty,
            _clock.GetUtcNow().UtcDateTime,
            cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(rule, null, "删除", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析请求里的规则类型（kebab-case 或枚举名）。
    /// </summary>
    private static AlertKind ParseKind(string value)
    {
        try
        {
            return AlertKindText.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new BusinessException(
                ErrorCodes.AlertRuleInvalid,
                $"未知的告警类型：{value}",
                exception);
        }
    }

    /// <summary>
    /// 解析请求里的主机名（空 = 全局）。
    /// </summary>
    private async Task<(Guid? HostId, string? HostName)> ResolveHostAsync(
        string? host,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(host))
        {
            return (null, null);
        }

        var entity = await _hosts.FindByNameAsync(host.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, "主机不存在");

        return (entity.Id, entity.Name);
    }

    /// <summary>
    /// 写一条配置变更审计（best-effort）。
    /// </summary>
    private Task RecordAuditAsync(
        AlertRule rule,
        string? hostName,
        string operation,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.AlertRuleUpdate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.AlertRule,
                TargetId = rule.Id.ToString("D"),
                TargetName = AlertKindText.ToText(rule.Kind),
                HostName = hostName,
                Detail = $"{operation}告警规则（{AlertKindText.ToText(rule.Kind)}，{(rule.Enabled ? "启用" : "停用")}）",
            },
            cancellationToken);

    /// <summary>
    /// 实体 → DTO。
    /// </summary>
    private static AlertRuleDto ToDto(
        AlertRule rule,
        string? hostName,
        IReadOnlyList<Guid> channelIds) => new(
        rule.Id,
        rule.HostId,
        hostName,
        AlertKindText.ToText(rule.Kind),
        rule.Enabled,
        rule.InfoThreshold,
        rule.WarningThreshold,
        rule.CriticalThreshold,
        AlertSeverityText.ToText(rule.Severity),
        rule.ConsecutiveSamples,
        rule.WindowSeconds,
        rule.CooldownSeconds,
        channelIds,
        rule.CreationTime);
}
