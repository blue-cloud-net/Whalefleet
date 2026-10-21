namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器健康检查状态（**唯一**判定口径）。
/// </summary>
/// <remarks>
/// <para><b>为什么收敛成一个枚举</b>：同一个「健康与否」在本项目里有<b>三处</b>消费方——
/// 容器列表的健康列、容器详情的健康卡片、主机概览的「健康 / 不健康」计数，
/// 而 0.11.0 的告警规则还要按它做阈值。每处各写一遍字符串判断，
/// 迟早会出现「列表说不健康、概览说健康」这种自相矛盾的界面。</para>
/// <para><b><see cref="None"/> 与「取不到」是两件事</b>：<see cref="None"/> 表示
/// 「这个容器根本没配健康检查」（永远不会变），而取不到（接口失败）由调用方
/// 用可空类型或「—」表达。混成一个值，界面就没法把「没配」与「查不到」分开表达。</para>
/// </remarks>
public enum ContainerHealthStatus
{
    /// <summary>未配置健康检查（Docker 的 <c>none</c>）。</summary>
    None,

    /// <summary>健康检查已配置，尚在启动宽限期内。</summary>
    Starting,

    /// <summary>健康检查通过。</summary>
    Healthy,

    /// <summary>健康检查连续失败。</summary>
    Unhealthy,
}

/// <summary>
/// 健康状态的解析入口（容器列表的 <c>Status</c> 文本与 inspect 的结构化字段共用）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要两套入口</b>：容器<b>列表</b>接口不返回结构化的健康字段，
/// 健康状态只出现在 <c>Status</c> 的括号里（<c>Up 2 hours (healthy)</c>）；
/// 而 <b>inspect</b> 给的是 <c>State.Health.Status</c>。两者必须映射到同一个枚举，
/// 否则「列表与详情不一致」会成为常态。</para>
/// <para><b>必须先判 <c>unhealthy</c></b>：<c>(healthy)</c> 是 <c>(unhealthy)</c> 的子串，
/// 反过来的话失败的健康检查会被算成健康——这正是这个功能最不该出的错。</para>
/// </remarks>
public static class ContainerHealth
{
    /// <summary>列表 <c>Status</c> 文本里的失败标记。</summary>
    private const string UnhealthyMarker = "(unhealthy)";

    /// <summary>列表 <c>Status</c> 文本里的通过标记。</summary>
    private const string HealthyMarker = "(healthy)";

    /// <summary>列表 <c>Status</c> 文本里的启动中标记（形如 <c>(health: starting)</c>）。</summary>
    private const string StartingMarker = "starting";

    /// <summary>
    /// 从容器列表的 <c>Status</c> 文本解析健康状态。
    /// </summary>
    /// <param name="status">列表项的状态文本（如 <c>Up 2 hours (healthy)</c>）。</param>
    /// <returns>健康状态；文本里没有健康标记时返回 <see cref="ContainerHealthStatus.None"/>。</returns>
    public static ContainerHealthStatus FromStatusText(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return ContainerHealthStatus.None;
        }

        if (status.Contains(UnhealthyMarker, StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthStatus.Unhealthy;
        }

        if (status.Contains(HealthyMarker, StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthStatus.Healthy;
        }

        // 启动中的写法是 `(health: starting)`，它不是 `(healthy)` 的子串
        if (status.Contains(StartingMarker, StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthStatus.Starting;
        }

        return ContainerHealthStatus.None;
    }

    /// <summary>
    /// 从 inspect 的 <c>State.Health.Status</c> 解析健康状态。
    /// </summary>
    /// <param name="status">Docker 给的状态文本（<c>none</c> / <c>starting</c> / <c>healthy</c> / <c>unhealthy</c>）。</param>
    /// <returns>健康状态；未配置或无法识别时返回 <see cref="ContainerHealthStatus.None"/>。</returns>
    /// <remarks>
    /// 容器没配健康检查时 Docker 不返回 <c>Health</c> 对象（或返回 <c>none</c>），
    /// 两种情况都归到 <see cref="ContainerHealthStatus.None"/>：
    /// 「没配」与「配了但状态未知」在这里没有可操作的区别。
    /// </remarks>
    public static ContainerHealthStatus FromInspect(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "starting" => ContainerHealthStatus.Starting,
        "healthy" => ContainerHealthStatus.Healthy,
        "unhealthy" => ContainerHealthStatus.Unhealthy,
        _ => ContainerHealthStatus.None,
    };
}
