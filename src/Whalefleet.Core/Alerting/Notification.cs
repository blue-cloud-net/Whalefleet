using Whalefleet.Core.Common;

namespace Whalefleet.Core.Alerting;

/// <summary>
/// 一条站内通知（用户可见的通知事件）。
/// </summary>
/// <remarks>
/// <para><b>存的是 i18n key + 参数，不是成品文案</b>：同一件事要在 toast、铃铛下拉、
/// 历史列表三处显示，文案必须能被翻译。存成品中文会让英文界面永远显示中文；
/// 存 key + 参数则渲染时才按当前语言翻译。</para>
/// <para><b>宿主主机的名字是快照</b>：主机 / 容器可能随后被删除或改名，
/// 通知历史仍要能读懂。</para>
/// <para><b>每用户已读状态在 <see cref="NotificationUserState"/></b>，
/// 不放在本表：一条通知会对多个人可见。</para>
/// </remarks>
public sealed class Notification : Entity<Guid>
{
    /// <summary>
    /// 消息 key 最大长度。
    /// </summary>
    public const int MaxMessageKeyLength = 160;

    /// <summary>
    /// 参数 JSON 最大长度。
    /// </summary>
    public const int MaxParamsLength = 16 * 1024;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private Notification()
    {
    }

    /// <summary>
    /// 来源类型。
    /// </summary>
    public NotificationSourceKind SourceKind { get; private set; }

    /// <summary>
    /// 严重度（用于红点与列表色调）。
    /// </summary>
    public AlertSeverity Severity { get; private set; }

    /// <summary>
    /// i18n key（如 <c>notification.alert.containerCpu</c>）。
    /// </summary>
    public string MessageKey { get; private set; } = String.Empty;

    /// <summary>
    /// 插值参数（JSON 对象，值只能是字符串或数字）。
    /// </summary>
    public string ParamsJson { get; private set; } = "{}";

    /// <summary>
    /// 关联主机主键（<c>hosts.id</c>）；全局通知为 <see langword="null"/>。
    /// </summary>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// 主机名快照。
    /// </summary>
    public string? HostName { get; private set; }

    /// <summary>
    /// 去重键；同一次事件的多个通道只落一条。为 <see langword="null"/> 时不去重。
    /// </summary>
    public string? DedupeKey { get; private set; }

    /// <summary>
    /// 发生时刻（UTC）。
    /// </summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>
    /// 创建一条通知。
    /// </summary>
    public static Notification Create(
        Guid id,
        NotificationSourceKind sourceKind,
        AlertSeverity severity,
        string messageKey,
        string paramsJson,
        Guid? hostId,
        string? hostName,
        string? dedupeKey,
        DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(paramsJson);

        if (messageKey.Length > MaxMessageKeyLength)
        {
            throw new ArgumentException($"消息 key 长度不得超过 {MaxMessageKeyLength}", nameof(messageKey));
        }

        if (paramsJson.Length > MaxParamsLength)
        {
            throw new ArgumentException($"通知参数长度不得超过 {MaxParamsLength}", nameof(paramsJson));
        }

        if (occurredAt.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("发生时刻必须是 UTC", nameof(occurredAt));
        }

        return new Notification
        {
            Id = id,
            SourceKind = sourceKind,
            Severity = severity,
            MessageKey = messageKey.Trim(),
            ParamsJson = paramsJson,
            HostId = hostId,
            HostName = hostName,
            DedupeKey = String.IsNullOrWhiteSpace(dedupeKey) ? null : dedupeKey.Trim(),
            OccurredAt = occurredAt,
        };
    }
}
