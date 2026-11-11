using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Alerting;

/// <summary>
/// 告警规则（可配置阈值 / 冷却期 / 启用状态 / 主机范围）。
/// </summary>
/// <remarks>
/// <para><b>一个作用域内同一种规则只有一条</b>：<c>host_id + kind</c> 组合唯一
/// （全局规则的 <c>host_id</c> 为 <see langword="null"/>）。这样「把 CPU 阈值调到 1%」
/// 不会因为存在两条同名规则而出现「改了这条、生效的是那条」。</para>
/// <para><b>阈值必须严格递增</b>：<c>info &lt; warning &lt; critical</c>。
/// 允许乱序会让「取最高命中级别」的判定失去意义。</para>
/// <para><b>主机离线没有数值阈值</b>：它的严重度由 <see cref="Severity"/> 指定，
/// 三个阈值字段为 <see langword="null"/>。登录失败激增则相反——三个阈值都是「次数」，
/// 且需要 <see cref="WindowSeconds"/> 指定统计窗口。</para>
/// </remarks>
public sealed class AlertRule : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 连续采样次数下限。
    /// </summary>
    public const int MinConsecutiveSamples = 1;

    /// <summary>
    /// 连续采样次数上限。
    /// </summary>
    public const int MaxConsecutiveSamples = 100;

    /// <summary>
    /// 冷却期上限（24 小时）。
    /// </summary>
    public const int MaxCooldownSeconds = 86_400;

    /// <summary>
    /// 统计窗口上限（24 小时）。
    /// </summary>
    public const int MaxWindowSeconds = 86_400;

    /// <summary>
    /// 作用域主机主键（<c>hosts.id</c>）；<see langword="null"/> 表示全局（所有主机）。
    /// </summary>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// 规则类型。
    /// </summary>
    public AlertKind Kind { get; private set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// info 级阈值；事件型规则（主机离线）为 <see langword="null"/>。
    /// </summary>
    public double? InfoThreshold { get; private set; }

    /// <summary>
    /// warning 级阈值；事件型规则（主机离线）为 <see langword="null"/>。
    /// </summary>
    public double? WarningThreshold { get; private set; }

    /// <summary>
    /// critical 级阈值；事件型规则（主机离线）为 <see langword="null"/>。
    /// </summary>
    public double? CriticalThreshold { get; private set; }

    /// <summary>
    /// 事件型规则使用的固定严重度（主机离线）。阈值型规则忽略本字段。
    /// </summary>
    public AlertSeverity Severity { get; private set; }

    /// <summary>
    /// 连续多少个采样（或连续多少次评估）命中才触发，用于压掉瞬时毛刺。
    /// </summary>
    public int ConsecutiveSamples { get; private set; }

    /// <summary>
    /// 统计窗口（秒）；仅登录失败激增使用，其余为 <see langword="null"/>。
    /// </summary>
    public int? WindowSeconds { get; private set; }

    /// <summary>
    /// 冷却期（秒）：同一规则在同一目标上触发后，该窗口内不再重复触发。
    /// </summary>
    public int CooldownSeconds { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private AlertRule()
    {
    }

    /// <summary>
    /// 新建规则。
    /// </summary>
    public static AlertRule Create(
        Guid id,
        Guid? hostId,
        AlertKind kind,
        bool enabled,
        double? infoThreshold,
        double? warningThreshold,
        double? criticalThreshold,
        AlertSeverity severity,
        int consecutiveSamples,
        int? windowSeconds,
        int cooldownSeconds)
    {
        var rule = new AlertRule { Id = id };

        rule.Apply(
            hostId,
            kind,
            enabled,
            infoThreshold,
            warningThreshold,
            criticalThreshold,
            severity,
            consecutiveSamples,
            windowSeconds,
            cooldownSeconds);

        return rule;
    }

    /// <summary>
    /// 更新规则（整组字段一起更新）。
    /// </summary>
    public void Update(
        Guid? hostId,
        AlertKind kind,
        bool enabled,
        double? infoThreshold,
        double? warningThreshold,
        double? criticalThreshold,
        AlertSeverity severity,
        int consecutiveSamples,
        int? windowSeconds,
        int cooldownSeconds) =>
        this.Apply(
            hostId,
            kind,
            enabled,
            infoThreshold,
            warningThreshold,
            criticalThreshold,
            severity,
            consecutiveSamples,
            windowSeconds,
            cooldownSeconds);

    /// <summary>
    /// 启用 / 停用（幂等）。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    public void SetEnabled(bool enabled) => this.Enabled = enabled;

    /// <summary>
    /// 应用一组字段并校验不变量。
    /// </summary>
    private void Apply(
        Guid? hostId,
        AlertKind kind,
        bool enabled,
        double? infoThreshold,
        double? warningThreshold,
        double? criticalThreshold,
        AlertSeverity severity,
        int consecutiveSamples,
        int? windowSeconds,
        int cooldownSeconds)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("未知的告警类型", nameof(kind));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentException("未知的告警严重度", nameof(severity));
        }

        if (consecutiveSamples is < MinConsecutiveSamples or > MaxConsecutiveSamples)
        {
            throw new ArgumentException(
                $"连续采样次数须在 {MinConsecutiveSamples}-{MaxConsecutiveSamples} 之间",
                nameof(consecutiveSamples));
        }

        if (cooldownSeconds is < 0 or > MaxCooldownSeconds)
        {
            throw new ArgumentException(
                $"冷却期须在 0-{MaxCooldownSeconds} 秒之间",
                nameof(cooldownSeconds));
        }

        var thresholds = ValidateThresholds(
            kind,
            infoThreshold,
            warningThreshold,
            criticalThreshold,
            windowSeconds);

        this.HostId = hostId;
        this.Kind = kind;
        this.Enabled = enabled;
        this.InfoThreshold = thresholds.Info;
        this.WarningThreshold = thresholds.Warning;
        this.CriticalThreshold = thresholds.Critical;
        this.Severity = severity;
        this.ConsecutiveSamples = consecutiveSamples;
        this.WindowSeconds = thresholds.Window;
        this.CooldownSeconds = cooldownSeconds;
    }

    /// <summary>
    /// 校验并规范化阈值字段。
    /// </summary>
    private static (double? Info, double? Warning, double? Critical, int? Window) ValidateThresholds(
        AlertKind kind,
        double? info,
        double? warning,
        double? critical,
        int? windowSeconds)
    {
        if (!AlertKindText.IsThresholdKind(kind))
        {
            // 主机离线：不接受任何数值阈值，避免界面显示一组「看起来能调、实际无所谓」的数字
            if (info is not null || warning is not null || critical is not null || windowSeconds is not null)
            {
                throw new ArgumentException("主机离线规则没有数值阈值与统计窗口", nameof(kind));
            }

            return (null, null, null, null);
        }

        if (info is null || warning is null || critical is null)
        {
            throw new ArgumentException("阈值型规则必须同时给出 info / warning / critical 三档阈值");
        }

        if (info <= 0 || warning <= 0 || critical <= 0)
        {
            throw new ArgumentException("阈值必须大于 0");
        }

        if (!(info < warning && warning < critical))
        {
            throw new ArgumentException("阈值必须严格递增：info < warning < critical");
        }

        if (kind == AlertKind.LoginFailureSurge)
        {
            if (windowSeconds is null or <= 0 or > MaxWindowSeconds)
            {
                throw new ArgumentException(
                    $"登录失败激增必须给出 1-{MaxWindowSeconds} 秒的统计窗口",
                    nameof(windowSeconds));
            }

            // 次数阈值必须是整数（1.5 次没有意义）
            if (info != Math.Floor(info!.Value)
                || warning != Math.Floor(warning!.Value)
                || critical != Math.Floor(critical!.Value))
            {
                throw new ArgumentException("登录失败次数阈值必须是整数");
            }
        }
        else if (windowSeconds is not null)
        {
            throw new ArgumentException("只有登录失败激增规则使用统计窗口", nameof(windowSeconds));
        }

        return (info, warning, critical, windowSeconds);
    }

    /// <summary>
    /// 给定当前值，选出命中的最高严重度；未达 info 阈值时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="value">当前值（百分比 / 字节每秒 / 次数）。</param>
    /// <returns>命中的严重度；未命中为 <see langword="null"/>。</returns>
    public AlertSeverity? ResolveSeverity(double value)
    {
        if (!AlertKindText.IsThresholdKind(this.Kind))
        {
            return this.Severity;
        }

        if (this.CriticalThreshold is { } critical && value >= critical)
        {
            return AlertSeverity.Critical;
        }

        if (this.WarningThreshold is { } warning && value >= warning)
        {
            return AlertSeverity.Warning;
        }

        if (this.InfoThreshold is { } info && value >= info)
        {
            return AlertSeverity.Info;
        }

        return null;
    }
}
