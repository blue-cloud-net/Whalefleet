namespace Whalefleet.Core.Alerting;

/// <summary>
/// 告警规则的类型（封闭集合）。
/// </summary>
/// <remarks>
/// <para><b>为什么是枚举而不是表</b>：规则类型对应的是<b>代码里真实存在的判定分支</b>。
/// 放进表里让运行时可加，只会产生「界面上有这条规则、但代码不会评估它」的幽灵配置。</para>
/// <para><b>本迭代只有 5 种</b>：主机离线、容器 CPU、容器内存、容器网络、登录失败激增。
/// 「备份失败」「TLS 证书即将过期」没有事实源（备份是外部脚本、TLS 由反向代理终止），
/// 因此不在此列。</para>
/// </remarks>
public enum AlertKind
{
    /// <summary>
    /// 主机离线（连通性探测连续失败）。
    /// </summary>
    HostOffline,

    /// <summary>
    /// 容器 CPU 使用率超阈值（百分比）。
    /// </summary>
    ContainerCpu,

    /// <summary>
    /// 容器内存使用率超阈值（占内存上限的百分比）。
    /// </summary>
    ContainerMemory,

    /// <summary>
    /// 容器网络吞吐超阈值（接收 + 发送 字节/秒）。
    /// </summary>
    ContainerNetwork,

    /// <summary>
    /// 登录失败激增（窗口内失败 + 限流次数）。
    /// </summary>
    LoginFailureSurge,
}

/// <summary>
/// <see cref="AlertKind"/> 的存储 / 接口文本形态（kebab-case）。
/// </summary>
/// <remarks>
/// 直接用 <c>ToString().ToLowerInvariant()</c> 会得到 <c>hostoffline</c>，
/// 与前端契约里的 <c>host-offline</c> 不一致。枚举名不能带连字符，
/// 因此显式映射一份，避免「库里一种写法、接口另一种写法」。
/// </remarks>
public static class AlertKindText
{
    /// <summary>
    /// 枚举 → 小写 kebab-case 文本。
    /// </summary>
    /// <param name="kind">告警类型。</param>
    /// <returns>文本形态。</returns>
    public static string ToText(AlertKind kind) => kind switch
    {
        AlertKind.HostOffline => "host-offline",
        AlertKind.ContainerCpu => "container-cpu",
        AlertKind.ContainerMemory => "container-memory",
        AlertKind.ContainerNetwork => "container-network",
        AlertKind.LoginFailureSurge => "login-failure-surge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的告警类型"),
    };

    /// <summary>
    /// 文本 → 枚举（大小写与首尾空白不敏感）。
    /// </summary>
    /// <param name="text">文本形态。</param>
    /// <returns>枚举值。</returns>
    /// <exception cref="FormatException">文本不是已知的告警类型。</exception>
    public static AlertKind Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // 库内存储用 PascalCase（Dapper 对枚举的物化走默认 Enum.Parse），
        // 接口契约用 kebab-case；两种形态都要认。
        if (Enum.TryParse<AlertKind>(text.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "host-offline" => AlertKind.HostOffline,
            "container-cpu" => AlertKind.ContainerCpu,
            "container-memory" => AlertKind.ContainerMemory,
            "container-network" => AlertKind.ContainerNetwork,
            "login-failure-surge" => AlertKind.LoginFailureSurge,
            _ => throw new FormatException($"未知的告警类型文本：{text}"),
        };
    }

    /// <summary>
    /// 是否为「阈值型」规则（有三级数值阈值）。
    /// </summary>
    /// <param name="kind">告警类型。</param>
    /// <returns>阈值型返回 <see langword="true"/>。</returns>
    public static bool IsThresholdKind(AlertKind kind) => kind is
        AlertKind.ContainerCpu or
        AlertKind.ContainerMemory or
        AlertKind.ContainerNetwork or
        AlertKind.LoginFailureSurge;

    /// <summary>
    /// 是否为容器指标型规则（依赖容器 stats 采样）。
    /// </summary>
    /// <param name="kind">告警类型。</param>
    /// <returns>指标型返回 <see langword="true"/>。</returns>
    public static bool IsContainerMetricKind(AlertKind kind) => kind is
        AlertKind.ContainerCpu or
        AlertKind.ContainerMemory or
        AlertKind.ContainerNetwork;
}
