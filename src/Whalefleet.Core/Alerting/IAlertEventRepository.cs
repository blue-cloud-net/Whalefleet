namespace Whalefleet.Core.Alerting;

/// <summary>
/// 告警事件仓储（只追加 + 只查）。
/// </summary>
/// <remarks>
/// 与审计表同构：不提供更新与删除——事件一旦触发就是事实。
/// 唯一会删数据的是按保留期裁剪（<see cref="TrimAsync"/>），由后台维护任务调用。
/// </remarks>
public interface IAlertEventRepository
{
    /// <summary>
    /// 追加一条事件。
    /// </summary>
    Task AddAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查同一规则在同一目标上的最近一次触发时刻（冷却判定）。
    /// </summary>
    /// <param name="ruleId">规则主键。</param>
    /// <param name="targetKey">目标键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最近触发时刻（UTC）；从未触发为 <see langword="null"/>。</returns>
    Task<DateTime?> GetLastOccurredAtAsync(
        Guid ruleId,
        string targetKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除早于指定时刻的事件。
    /// </summary>
    /// <param name="beforeUtc">保留边界（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除行数。</returns>
    Task<int> TrimAsync(DateTime beforeUtc, CancellationToken cancellationToken = default);
}
