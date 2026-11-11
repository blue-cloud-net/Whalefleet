namespace Whalefleet.Application.Alerting;

/// <summary>
/// 告警评估选项（配置节 <c>Alerts</c>）。
/// </summary>
public sealed class AlertsOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Alerts";

    /// <summary>
    /// 周期性评估间隔（秒）：主机离线与登录失败激增按这个节奏检查。
    /// </summary>
    public int EvaluationIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 容器指标订阅的同步间隔（秒）：重新枚举主机 / 容器，增删 stats 订阅。
    /// </summary>
    public int ContainerResyncSeconds { get; set; } = 60;

    /// <summary>
    /// 单台主机最多同时监控多少个容器（指标类规则）。
    /// </summary>
    /// <remarks>
    /// 每个被监控的容器占一条到 Docker 的 stats 长连接。没有上限时，
    /// 一台有上千容器的机器会把守护进程拖垮。
    /// </remarks>
    public int MaxMonitoredContainersPerHost { get; set; } = 100;

    /// <summary>
    /// 告警事件与通知的保留天数。
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// 配置是否合法。
    /// </summary>
    public bool IsValid() =>
        this.EvaluationIntervalSeconds > 0 &&
        this.ContainerResyncSeconds > 0 &&
        this.MaxMonitoredContainersPerHost > 0 &&
        this.RetentionDays > 0;
}
