using Whalefleet.Core.Alerting;

namespace Whalefleet.Application.Alerting;

/// <summary>
/// 站内通知写入（落库 + 发布实时事件）。
/// </summary>
/// <remarks>
/// 抽出窄接口是为了让「通知来源」（容器事件 / 编排任务 / 告警评估）只依赖写入能力，
/// 而不需要构造整个 <see cref="NotificationService"/>（那个类还依赖权限与主机仓储，
/// 那些只在查询侧才用得上）。测试也能因此只替换写入端。
/// </remarks>
public interface INotificationWriter
{
    /// <summary>
    /// 创建一条通知并发布实时事件。
    /// </summary>
    Task<Notification> CreateAsync(NotificationDraft draft, CancellationToken cancellationToken = default);
}
