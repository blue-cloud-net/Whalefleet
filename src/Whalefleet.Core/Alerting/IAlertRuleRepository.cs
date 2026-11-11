namespace Whalefleet.Core.Alerting;

/// <summary>
/// 告警规则仓储（持久化契约）。
/// </summary>
public interface IAlertRuleRepository : Common.IRepository<AlertRule, Guid>
{
    /// <summary>
    /// 列出某台主机的规则 + 全局规则。
    /// </summary>
    /// <param name="hostId">主机主键；<see langword="null"/> 表示只看全局规则。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>规则列表（全局在前，再按类型排序）。</returns>
    Task<IReadOnlyList<AlertRule>> ListForHostAsync(Guid? hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断同一作用域内是否已存在同类型规则。
    /// </summary>
    /// <param name="hostId">主机主键；<see langword="null"/> 表示全局。</param>
    /// <param name="kind">规则类型。</param>
    /// <param name="excludeId">更新时排除自身；新建传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsAsync(
        Guid? hostId,
        AlertKind kind,
        Guid? excludeId,
        CancellationToken cancellationToken = default);
}
