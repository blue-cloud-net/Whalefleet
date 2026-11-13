using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Webhooks;

/// <summary>
/// 容器重建 Webhook（让外部事件触发某个容器被重建）。
/// </summary>
/// <remarks>
/// <para><b>为什么令牌绑「容器名」而不是容器 ID</b>：重建会让容器 ID 一定改变，
/// 绑 ID 等于「令牌下一次就失效」——而重建 Webhook 的高频用法恰恰是
/// 「反复触发同一个容器重建」（CI 推完代码就重建一次）。
/// 容器名在重建前后不变，因此它是唯一稳定的标识。</para>
/// <para><b>代价：改名会让令牌失效</b>。这是可接受的——
/// 界面必须明确提示「改���后需要重新生成令牌」。</para>
/// <para><b>Webhook 令牌只存哈希</b>：理由同 Compose 项目的 Webhook 令牌——
/// 令牌等价于「对这台主机上这个容器的重建权限」，泄露的代价是任何人都能重放你的重建。
/// 明文只在生成那一刻返回一次，库里留 SHA-256 与前缀提示。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class ContainerWebhook : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 容器名最大长度（字符数）。
    /// </summary>
    public const int MaxContainerNameLength = 255;

    /// <summary>
    /// Webhook 令牌明文的最大长度（字符数）。
    /// </summary>
    public const int MaxTokenLength = 128;

    /// <summary>
    /// Webhook 令牌提示（界面展示用）最大长度。
    /// </summary>
    public const int MaxTokenHintLength = 12;

    /// <summary>
    /// 所属主机主键（<c>hosts.id</c>）。
    /// </summary>
    public Guid HostId { get; private set; }

    /// <summary>
    /// 目标容器名（Docker 的容器名，不含前导 <c>/</c>）。
    /// </summary>
    public string ContainerName { get; private set; } = default!;

    /// <summary>
    /// Webhook 令牌的 SHA-256 哈希（小写十六进制）。
    /// </summary>
    public string WebhookTokenHash { get; private set; } = default!;

    /// <summary>
    /// Webhook 令牌提示（明文前若干位）。
    /// </summary>
    public string WebhookTokenHint { get; private set; } = default!;

    /// <summary>
    /// Webhook 是否启用。
    /// </summary>
    public bool WebhookEnabled { get; private set; }

    /// <summary>
    /// Webhook 最近一次被触发的时间（UTC）；从未触发为 <see langword="null"/>。
    /// </summary>
    public DateTime? WebhookLastUsedAt { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Register"/>。
    /// </summary>
    private ContainerWebhook()
    {
    }

    /// <summary>
    /// 登记一个容器重建 Webhook。
    /// </summary>
    /// <param name="id">主键。</param>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="containerName">目标容器名。</param>
    /// <param name="tokenHash">令牌的 SHA-256 哈希（小写十六进制）。</param>
    /// <param name="tokenHint">令牌提示（明文前若干位）。</param>
    /// <returns>新建的 Webhook 实体。</returns>
    /// <exception cref="ArgumentException">容器名或令牌哈希非法。</exception>
    public static ContainerWebhook Register(
        Guid id,
        Guid hostId,
        string containerName,
        string tokenHash,
        string tokenHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHint);

        var trimmedName = containerName.Trim().TrimStart('/');

        if (trimmedName.Length > MaxContainerNameLength)
        {
            throw new ArgumentException(
                $"容器名不得超过 {MaxContainerNameLength} 个字符",
                nameof(containerName));
        }

        if (tokenHash.Length != 64 || !tokenHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Webhook 令牌哈希必须是 64 位十六进制", nameof(tokenHash));
        }

        if (tokenHint.Length > MaxTokenHintLength)
        {
            throw new ArgumentException(
                $"Webhook 令牌提示不得超过 {MaxTokenHintLength} 个字符",
                nameof(tokenHint));
        }

        return new ContainerWebhook
        {
            Id = id,
            HostId = hostId,
            ContainerName = trimmedName,
            WebhookTokenHash = tokenHash.ToLowerInvariant(),
            WebhookTokenHint = tokenHint,
            WebhookEnabled = true,
        };
    }

    /// <summary>
    /// 吊销 Webhook。
    /// </summary>
    /// <remarks>
    /// <b>吊销要连哈希一起清掉</b>：只把 <see cref="WebhookEnabled"/> 置回 <see langword="false"/>
    /// 会让哈希继续占住唯一索引，而「已吊销」与「不存在」对外必须表现为同一件事。
    /// </remarks>
    public void Revoke()
    {
        this.WebhookTokenHash = String.Empty;
        this.WebhookTokenHint = String.Empty;
        this.WebhookEnabled = false;
    }

    /// <summary>
    /// 记录 Webhook 被触发的时间。
    /// </summary>
    /// <param name="usedAt">触发时刻（UTC）。</param>
    public void MarkUsed(DateTime usedAt) => this.WebhookLastUsedAt = usedAt;
}
