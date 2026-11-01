using System.Diagnostics.CodeAnalysis;

using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Stacks;

/// <summary>
/// 编排 Stack（一个 Docker Compose 项目在本平台里的记录）。
/// </summary>
/// <remarks>
/// <para><b>本表存的是「YAML 原文」，不是「容器的当前状态」</b>：
/// 容器状态的事实源永远是守护进程（靠 <c>com.docker.compose.project</c> 标签发现），
/// 本表只负责两件守护进程管不了的事——<b>记住原始 YAML</b>（更新与 webhook 重放都要靠它）
/// 与 <b>记住「这个项目是本平台部署的」</b>（<c>managed</c> 的判据）。</para>
/// <para><b>因此本表可以与现实不一致</b>：用户可能在守护进程侧手工删了容器，
/// 或在别的机器上部署了同名项目。本表<b>不</b>试图成为真相，只提供「我们认为该怎么部署它」的依据。
/// 列表接口会把两边的信息合并呈现（<c>managed</c> / <c>status</c> / 容器数）。</para>
/// <para><b>名称即 Compose 项目名</b>：它会作为 <c>com.docker.compose.project</c> 标签值
/// 落到容器、网络与卷上，因此必须符合 Compose 的命名规则
/// （小写字母、数字、<c>-</c>、<c>_</c>，且以字母或数字开头）——
/// 放行别的字符会让 <c>docker compose</c> 侧无法识别这个项目，
/// 症状是「界面上部署成功了，但 <c>docker compose ps</c> 里没有它」。</para>
/// <para><b>Webhook 令牌只存哈希</b>：明文只在生成那一刻返回一次，库里留 SHA-256 与
/// 前缀提示（供界面显示「哪一个令牌」而不泄露它）。理由同刷新令牌——
/// 令牌等价于「对这台主机的部署权限」，泄露的代价是任何人都能重放你的编排。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Stack 是本项目的领域名词（Docker Compose 编排项目），非 CA1711 所针对的系统命名空间后缀。")]
public sealed class Stack : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// Stack 名称最小长度。
    /// </summary>
    public const int MinNameLength = 1;

    /// <summary>
    /// Stack 名称最大长度。
    /// </summary>
    /// <remarks>与 Docker Compose 项目名的实际上限一致（63 字符，留给前缀拼接）。</remarks>
    public const int MaxNameLength = 63;

    /// <summary>
    /// Compose 文件内容的长度上限（字符数）。
    /// </summary>
    /// <remarks>
    /// 上限 256 KB：Compose 文件里嵌证书 / 密钥时可能偏大，但再大就不是编排文件而是数据文件，
    /// 而数据文件应当由 0.15.0 的「.env 变量与密钥」能力承载（不入库、单独管理）。
    /// </remarks>
    public const int MaxComposeContentLength = 256 * 1024;

    /// <summary>
    /// Webhook 令牌明文的最大长度（字符数）。
    /// </summary>
    public const int MaxWebhookTokenLength = 128;

    /// <summary>
    /// Webhook 令牌提示（界面展示用）最大长度。
    /// </summary>
    public const int MaxWebhookTokenHintLength = 12;

    /// <summary>
    /// 所属主机主键（<c>hosts.id</c>）。
    /// </summary>
    /// <remarks>
    /// 存 <c>hosts.id</c>（Guid）而<b>不是</b>主机名：主机名可改，编排项目与主机的从属关系不该
    /// 因改名断掉。与 <c>host_tag_assignments.host_id</c> 同口径。
    /// </remarks>
    public Guid HostId { get; private set; }

    /// <summary>
    /// Stack 名称（= Compose 项目名）。
    /// </summary>
    public string Name { get; private set; } = default!;

    /// <summary>
    /// Compose 文件原文（YAML）。
    /// </summary>
    public string ComposeContent { get; private set; } = default!;

    /// <summary>
    /// Webhook 令牌的 SHA-256 哈希（小写十六进制）；未启用时为 <see langword="null"/>。
    /// </summary>
    public string? WebhookTokenHash { get; private set; }

    /// <summary>
    /// Webhook 令牌提示（明文前若干位）；未启用时为 <see langword="null"/>。
    /// </summary>
    public string? WebhookTokenHint { get; private set; }

    /// <summary>
    /// Webhook 是否启用。
    /// </summary>
    public bool WebhookEnabled { get; private set; }

    /// <summary>
    /// Webhook 最近一次被触发的时间（UTC）；从未触发为 <see langword="null"/>。
    /// </summary>
    public DateTime? WebhookLastUsedAt { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private Stack()
    {
    }

    /// <summary>
    /// 新建 Stack。
    /// </summary>
    /// <param name="id">Stack 主键。</param>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="name">Stack 名称（Compose 项目名）。</param>
    /// <param name="composeContent">Compose 文件原文。</param>
    /// <returns>新建的 Stack 实体。</returns>
    /// <exception cref="ArgumentException">任一字段非法。</exception>
    public static Stack Create(Guid id, Guid hostId, string name, string composeContent) => new()
    {
        Id = id,
        HostId = hostId,
        Name = NormalizeName(name),
        ComposeContent = NormalizeComposeContent(composeContent),
    };

    /// <summary>
    /// 替换 Compose 文件内容。
    /// </summary>
    /// <param name="composeContent">新的 Compose 文件原文。</param>
    /// <exception cref="ArgumentException">内容为空或超长。</exception>
    public void ReplaceContent(string composeContent) =>
        this.ComposeContent = NormalizeComposeContent(composeContent);

    /// <summary>
    /// 启用或更换 Webhook 令牌。
    /// </summary>
    /// <param name="tokenHash">令牌的 SHA-256 哈希（小写十六进制）。</param>
    /// <param name="tokenHint">令牌提示（明文前若干位）。</param>
    /// <exception cref="ArgumentException">哈希或提示非法。</exception>
    /// <remarks>
    /// <b>轮换即刻失效旧令牌</b>：哈希被整体替换，旧令牌从此查无此行，
    /// 无需额外的「历史令牌」表——那只是让作废的令牌还能被试探。
    /// </remarks>
    public void SetWebhook(string tokenHash, string tokenHint)
    {
        this.WebhookTokenHash = NormalizeTokenHash(tokenHash);
        this.WebhookTokenHint = NormalizeTokenHint(tokenHint);
        this.WebhookEnabled = true;
    }

    /// <summary>
    /// 吊销 Webhook。
    /// </summary>
    /// <remarks>
    /// <b>吊销要连哈希一起清掉</b>：只把 <see cref="WebhookEnabled"/> 置回 <see langword="false"/>
    /// 而留着哈希，会让「吊销后重建」与「吊销前有效」在库里长得一样，
    /// 而唯一索引（<c>WHERE webhook_enabled = 1</c>）之外也没有别的防线。
    /// </remarks>
    public void RevokeWebhook()
    {
        this.WebhookTokenHash = null;
        this.WebhookTokenHint = null;
        this.WebhookEnabled = false;
    }

    /// <summary>
    /// 记录 Webhook 被触发的时间。
    /// </summary>
    /// <param name="usedAt">触发时刻（UTC）。</param>
    public void MarkWebhookUsed(DateTime usedAt) => this.WebhookLastUsedAt = usedAt;

    /// <summary>
    /// 规范化并校验 Stack 名称。
    /// </summary>
    /// <param name="name">原始名称。</param>
    /// <returns>规范化后的名称。</returns>
    /// <exception cref="ArgumentException">名称不符合 Compose 项目名规则。</exception>
    /// <remarks>
    /// <b>规则抄自 Compose 规范</b>：<c>^[a-z0-9][a-z0-9_-]*$</c>。
    /// 大写字母与起始的 <c>-</c> / <c>_</c> 都不合法——守护进程不会拦，
    /// 但标签值会让 <c>docker compose</c> 认不出这个项目。
    /// </remarks>
    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (trimmed.Length is < MinNameLength or > MaxNameLength)
        {
            throw new ArgumentException(
                $"Stack 名称长度须在 {MinNameLength}-{MaxNameLength} 之间",
                nameof(name));
        }

        if (!IsAsciiLetterOrDigit(trimmed[0]))
        {
            throw new ArgumentException(
                "Stack 名称须以字母或数字开头（这是 Compose 项目名的规则）",
                nameof(name));
        }

        foreach (var character in trimmed)
        {
            var allowed = IsAsciiLetterOrDigit(character) || character is '-' or '_';

            if (!allowed)
            {
                throw new ArgumentException(
                    "Stack 名称只能包含小写字母、数字、连字符与下划线（这是 Compose 项目名的规则）",
                    nameof(name));
            }
        }

        return trimmed;
    }

    /// <summary>
    /// 判断字符是否 ASCII 字母或数字。
    /// </summary>
    /// <param name="character">待判断字符。</param>
    /// <returns>是则返回 <see langword="true"/>。</returns>
    private static bool IsAsciiLetterOrDigit(char character) =>
        character is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    /// <summary>
    /// 规范化并校验 Compose 文件内容。
    /// </summary>
    /// <param name="content">原始内容。</param>
    /// <returns>去除首尾空白后的内容。</returns>
    /// <exception cref="ArgumentException">内容为空或超长。</exception>
    private static string NormalizeComposeContent(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var trimmed = content.Trim();

        if (trimmed.Length > MaxComposeContentLength)
        {
            throw new ArgumentException(
                $"Compose 文件内容不得超过 {MaxComposeContentLength} 个字符",
                nameof(content));
        }

        return trimmed;
    }

    /// <summary>
    /// 规范化并校验令牌哈希。
    /// </summary>
    /// <param name="hash">原始哈希。</param>
    /// <returns>小写十六进制哈希。</returns>
    /// <exception cref="ArgumentException">哈希不是 64 位十六进制。</exception>
    private static string NormalizeTokenHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var trimmed = hash.Trim();

        if (trimmed.Length != 64 || !trimmed.All(static c => Char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("Webhook 令牌哈希必须是 64 位十六进制", nameof(hash));
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// 规范化并校验令牌提示。
    /// </summary>
    /// <param name="hint">原始提示。</param>
    /// <returns>规范化后的提示。</returns>
    /// <exception cref="ArgumentException">提示为空或超长。</exception>
    private static string NormalizeTokenHint(string hint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hint);

        var trimmed = hint.Trim();

        if (trimmed.Length > MaxWebhookTokenHintLength)
        {
            throw new ArgumentException(
                $"Webhook 令牌提示不得超过 {MaxWebhookTokenHintLength} 个字符",
                nameof(hint));
        }

        return trimmed;
    }
}
