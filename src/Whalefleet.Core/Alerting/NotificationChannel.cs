using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Alerting;

/// <summary>
/// 通知渠道（Webhook / 邮件）。
/// </summary>
/// <remarks>
/// <para><b>敏感字段加密落库</b>：<see cref="ConfigJson"/> 里的 Webhook 鉴权令牌与 SMTP 密码
/// 在写入前由应用层用 <c>ISecretProtector</c>（与仓库口令同一把密钥）加密。
/// 实体只负责保存已加密的文本与名称 / 类型 / 启停状态。</para>
/// <para><b>密码永不进日志 / 响应</b>：接口返回时只给「是否配置」与脱敏后的地址，
/// 不回传配置原文。</para>
/// </remarks>
public sealed class NotificationChannel : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 渠道名最大长度。
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// 配置 JSON 最大长度。
    /// </summary>
    public const int MaxConfigLength = 16 * 1024;

    /// <summary>
    /// 渠道名（去首尾空白后保存，唯一性不区分大小写）。
    /// </summary>
    public string Name { get; private set; } = String.Empty;

    /// <summary>
    /// 渠道类型。
    /// </summary>
    public NotificationChannelKind Kind { get; private set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// 加密后的配置 JSON。
    /// </summary>
    public string ConfigJson { get; private set; } = String.Empty;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private NotificationChannel()
    {
    }

    /// <summary>
    /// 新建渠道。
    /// </summary>
    public static NotificationChannel Create(
        Guid id,
        string name,
        NotificationChannelKind kind,
        bool enabled,
        string configJson)
    {
        var channel = new NotificationChannel { Id = id };

        channel.Apply(name, kind, enabled, configJson);

        return channel;
    }

    /// <summary>
    /// 更新渠道。
    /// </summary>
    public void Update(string name, NotificationChannelKind kind, bool enabled, string configJson) =>
        this.Apply(name, kind, enabled, configJson);

    /// <summary>
    /// 应用一组字段并校验不变量。
    /// </summary>
    private void Apply(string name, NotificationChannelKind kind, bool enabled, string configJson)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("未知的通知渠道类型", nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"渠道名长度不得超过 {MaxNameLength}", nameof(name));
        }

        if (trimmed.Any(Char.IsControl))
        {
            throw new ArgumentException("渠道名不得包含控制字符", nameof(name));
        }

        if (configJson.Length > MaxConfigLength)
        {
            throw new ArgumentException($"渠道配置长度不得超过 {MaxConfigLength}", nameof(configJson));
        }

        this.Name = trimmed;
        this.Kind = kind;
        this.Enabled = enabled;
        this.ConfigJson = configJson;
    }
}
