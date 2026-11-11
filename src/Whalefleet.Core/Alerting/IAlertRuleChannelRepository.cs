namespace Whalefleet.Core.Alerting;

/// <summary>
/// 告警规则 ↔ 通知渠道 的关联仓储。
/// </summary>
public interface IAlertRuleChannelRepository
{
    /// <summary>
    /// 查某条规则绑定的渠道。
    /// </summary>
    Task<IReadOnlyList<Guid>> GetChannelIdsAsync(Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量查多条规则绑定的渠道。
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetChannelIdsAsync(
        IReadOnlyCollection<Guid> ruleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 整体替换某条规则的渠道绑定（先删后插）。
    /// </summary>
    Task ReplaceAsync(
        Guid ruleId,
        IReadOnlyCollection<Guid> channelIds,
        Guid creatorId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
