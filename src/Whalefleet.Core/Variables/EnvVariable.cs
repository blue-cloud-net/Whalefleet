using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Variables;

/// <summary>
/// 环境变量（全局变量或某个 Compose 项目自己的变量）。
/// </summary>
/// <remarks>
/// <para><b>两档作用域</b>：全局变量（<see cref="HostId"/> 为 <see langword="null"/>）
/// 对所有 Compose 项目生效；项目级变量绑定「主机 + Compose 项目名」。
/// 项目级变量<b>刻意不绑 <c>composes.id</c></b>：那个主键会随「删除项目 → 重新部署」而变，
/// 绑它等于「删一次项目就丢一遍变量」。绑定「主机 + 项目名」与 Compose 自身的
/// <c>.env</c> 语义一致——<c>docker compose down</c> 也不会删掉 <c>.env</c>。</para>
/// <para><b>值一律加密落库</b>：实体里只有密文（<see cref="EncryptedValue"/>），
/// 明文只在「真的要注入 Compose」的那一刻解开，且不进日志、不进响应。
/// 加密由 <c>Infrastructure/Security</c> 的保护器完成（复用仓库口令那把密钥）。</para>
/// <para><b>「是否可被 Webhook 覆盖」是不可变量</b>：密钥<b>永远</b>不可被覆盖。
/// Webhook 是匿名入口（凭据就是 URL 里的令牌），允许覆盖密钥等于开了一条
/// 「远程改数据库口令」的通道。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class EnvVariable : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 变量值最大长度（字符数，明文口径）。
    /// </summary>
    /// <remarks>
    /// 上限 4 KB：足够放证书片段与连接串，又不至于让变量表变成数据表。
    /// </remarks>
    public const int MaxValueLength = 4096;

    /// <summary>
    /// 备注最大长度（字符数）。
    /// </summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>
    /// 变量名。
    /// </summary>
    /// <remarks>
    /// 字符集与长度由 <see cref="EnvVariableName"/> 统一把关；此处存的是**已校验**的形态。
    /// </remarks>
    public string Name { get; private set; } = default!;

    /// <summary>
    /// 所属主机主键（<c>hosts.id</c>）；<see langword="null"/> 表示全局变量。
    /// </summary>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// 所属 Compose 项目名；<see langword="null"/> 表示全局变量。
    /// </summary>
    public string? ComposeName { get; private set; }

    /// <summary>
    /// 变量值的密文；<see langword="null"/> 表示「尚未设置值」。
    /// </summary>
    /// <remarks>
    /// <b>密文，不是明文</b>。解密只发生在「真的要注入 Compose」的那一刻，
    /// 且解密结果不落日志、不进响应、不进异常消息。
    /// </remarks>
    public string? EncryptedValue { get; private set; }

    /// <summary>
    /// 是否为密钥（界面遮罩显示、禁止 Webhook 覆盖）。
    /// </summary>
    public bool IsSecret { get; private set; }

    /// <summary>
    /// 是否允许被 Webhook 的查询串覆盖。
    /// </summary>
    /// <remarks>
    /// <b>密钥恒为 <see langword="false"/></b>：见类型注释里的理由。
    /// </remarks>
    public bool WebhookOverridable { get; private set; }

    /// <summary>
    /// 备注；可为空。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 是否为全局变量。
    /// </summary>
    public bool IsGlobal => this.HostId is null;

    /// <summary>
    /// 是否已设置值。
    /// </summary>
    public bool HasValue => this.EncryptedValue is not null;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="CreateGlobal"/> 或
    /// <see cref="CreateForProject"/>。
    /// </summary>
    private EnvVariable()
    {
    }

    /// <summary>
    /// 新建全局变量。
    /// </summary>
    /// <param name="id">主键。</param>
    /// <param name="name">变量名。</param>
    /// <param name="encryptedValue">变量值的密文；<see langword="null"/> 表示尚未设置值。</param>
    /// <param name="isSecret">是否为密钥。</param>
    /// <param name="webhookOverridable">是否允许被 Webhook 覆盖（密钥恒为否）。</param>
    /// <param name="description">备注；可为空。</param>
    /// <returns>新建的变量实体。</returns>
    /// <exception cref="ArgumentException">变量名或值非法。</exception>
    public static EnvVariable CreateGlobal(
        Guid id,
        string name,
        string? encryptedValue,
        bool isSecret = false,
        bool webhookOverridable = false,
        string? description = null) =>
        new()
        {
            Id = id,
            Name = EnvVariableName.Parse(name).Value,
            EncryptedValue = NormalizeValue(encryptedValue),
            IsSecret = isSecret,
            WebhookOverridable = webhookOverridable && !isSecret,
            Description = NormalizeDescription(description),
        };

    /// <summary>
    /// 新建某个 Compose 项目自己的变量。
    /// </summary>
    /// <param name="id">主键。</param>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="composeName">所属 Compose 项目名。</param>
    /// <param name="name">变量名。</param>
    /// <param name="encryptedValue">变量值的密文；<see langword="null"/> 表示尚未设置值。</param>
    /// <param name="isSecret">是否为密钥。</param>
    /// <param name="webhookOverridable">是否允许被 Webhook 覆盖（密钥恒为否）。</param>
    /// <param name="description">备注；可为空。</param>
    /// <returns>新建的变量实体。</returns>
    /// <exception cref="ArgumentException">变量名、项目名或值非法。</exception>
    public static EnvVariable CreateForProject(
        Guid id,
        Guid hostId,
        string composeName,
        string name,
        string? encryptedValue,
        bool isSecret = false,
        bool webhookOverridable = false,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeName);

        return new EnvVariable
        {
            Id = id,
            HostId = hostId,
            ComposeName = composeName.Trim(),
            Name = EnvVariableName.Parse(name).Value,
            EncryptedValue = NormalizeValue(encryptedValue),
            IsSecret = isSecret,
            WebhookOverridable = webhookOverridable && !isSecret,
            Description = NormalizeDescription(description),
        };
    }

    /// <summary>
    /// 替换变量值。
    /// </summary>
    /// <param name="encryptedValue">新值的密文；<see langword="null"/> 表示清空。</param>
    /// <exception cref="ArgumentException">值非法。</exception>
    public void SetValue(string? encryptedValue) => this.EncryptedValue = NormalizeValue(encryptedValue);

    /// <summary>
    /// 清空变量值。
    /// </summary>
    public void ClearValue() => this.EncryptedValue = null;

    /// <summary>
    /// 修改元数据（密钥标记、Webhook 覆盖许可、备注）。
    /// </summary>
    /// <param name="isSecret">是否为密钥。</param>
    /// <param name="webhookOverridable">是否允许被 Webhook 覆盖（密钥恒为否）。</param>
    /// <param name="description">备注；可为空。</param>
    /// <exception cref="ArgumentException">备注超长。</exception>
    public void UpdateMetadata(bool isSecret, bool webhookOverridable, string? description)
    {
        this.IsSecret = isSecret;
        this.WebhookOverridable = webhookOverridable && !isSecret;
        this.Description = NormalizeDescription(description);
    }

    /// <summary>
    /// 规范化密文：空串视为「未设置值」。
    /// </summary>
    /// <param name="encryptedValue">密文。</param>
    /// <returns>规范化后的密文或 <see langword="null"/>。</returns>
    private static string? NormalizeValue(string? encryptedValue)
    {
        if (encryptedValue is null)
        {
            return null;
        }

        if (encryptedValue.Length == 0)
        {
            return null;
        }

        if (encryptedValue.Length > MaxValueLength * 2)
        {
            throw new ArgumentException($"变量值过长（密文长度不得超过 {MaxValueLength * 2}）", nameof(encryptedValue));
        }

        return encryptedValue;
    }

    /// <summary>
    /// 规范化备注。
    /// </summary>
    /// <param name="description">备注。</param>
    /// <returns>规范化后的备注或 <see langword="null"/>。</returns>
    private static string? NormalizeDescription(string? description)
    {
        if (String.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();

        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"备注不得超过 {MaxDescriptionLength} 个字符", nameof(description));
        }

        return trimmed;
    }
}
