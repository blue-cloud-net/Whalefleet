namespace Whalefleet.Core.Alerting;

/// <summary>
/// 站内通知的来源类型。
/// </summary>
/// <remarks>
/// 迁移现有两种实时通知（容器异常退出、Compose 任务失败）与新增的告警触发，
/// 统一走同一张 <c>notifications</c> 表，铃铛才有完整历史。
/// </remarks>
public enum NotificationSourceKind
{
    /// <summary>
    /// 告警规则触发。
    /// </summary>
    AlertFired,

    /// <summary>
    /// 容器异常退出。
    /// </summary>
    ContainerDied,

    /// <summary>
    /// Compose 项目任务失败。
    /// </summary>
    ComposeTaskFailed,

    /// <summary>
    /// 镜像有新版本（仓库摘要变了）。
    /// </summary>
    ImageUpdateAvailable,

    /// <summary>
    /// 镜像更新检查或自动应用失败。
    /// </summary>
    ImageUpdateFailed,
}

/// <summary>
/// <see cref="NotificationSourceKind"/> 的存储 / 接口文本形态（kebab-case）。
/// </summary>
public static class NotificationSourceKindText
{
    /// <summary>
    /// 枚举 → 小写 kebab-case 文本。
    /// </summary>
    /// <param name="kind">来源类型。</param>
    /// <returns>文本形态。</returns>
    public static string ToText(NotificationSourceKind kind) => kind switch
    {
        NotificationSourceKind.AlertFired => "alert-fired",
        NotificationSourceKind.ContainerDied => "container-died",
        NotificationSourceKind.ComposeTaskFailed => "compose-task-failed",
        NotificationSourceKind.ImageUpdateAvailable => "image-update-available",
        NotificationSourceKind.ImageUpdateFailed => "image-update-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的通知来源类型"),
    };

    /// <summary>
    /// 文本 → 枚举（大小写与首尾空白不敏感）。
    /// </summary>
    /// <param name="text">文本形态。</param>
    /// <returns>枚举值。</returns>
    /// <exception cref="FormatException">文本不是已知的来源类型。</exception>
    public static NotificationSourceKind Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (Enum.TryParse<NotificationSourceKind>(text.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "alert-fired" => NotificationSourceKind.AlertFired,
            "container-died" => NotificationSourceKind.ContainerDied,
            "compose-task-failed" => NotificationSourceKind.ComposeTaskFailed,
            "image-update-available" => NotificationSourceKind.ImageUpdateAvailable,
            "image-update-failed" => NotificationSourceKind.ImageUpdateFailed,
            _ => throw new FormatException($"未知的通知来源类型文本：{text}"),
        };
    }
}
