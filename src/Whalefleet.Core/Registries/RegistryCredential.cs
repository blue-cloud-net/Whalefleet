using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Registries;

/// <summary>
/// 私有 Registry 的连接配置（地址 + 账号 + 口令）。
/// </summary>
/// <remarks>
/// <para><b>口令以密文形式存在于实体里</b>：本实体只持有<b>加密后</b>的文本，
/// 加密由 <c>Infrastructure/Security</c> 里的保护器完成。实体不持有明文，
/// 因此「把实体整个序列化进日志」也<b>不会</b>泄露口令——这与
/// <c>AdminBootstrapper</c> 拒绝把初始密码写进日志是同一条纪律。</para>
/// <para><b>名称与地址是两回事</b>：<see cref="Name"/> 是管理员起的<b>别名</b>
/// （如「内网 Harbor」），<see cref="ServerAddress"/> 是机器可读的地址
/// （<c>harbor.internal:5000</c>）。别名可读性好，地址则必须逐字稳定——
/// 推镜像时拼引用、认证时传给 Docker，用的都是地址。</para>
/// <para><b>地址唯一</b>：同一个地址存两条配置会让「推镜像时该用哪条凭据」变成
/// 随机行为。数据库层有唯一索引兜底。</para>
/// <para><b>删除是软删除</b>：历史审计要留；地址在删除后可再次使用。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class RegistryCredential : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 别名最小长度（去首尾空白后）。
    /// </summary>
    public const int MinNameLength = 1;

    /// <summary>
    /// 别名最大长度（去首尾空白后）。
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// 账号最大长度（去首尾空白后）。
    /// </summary>
    public const int MaxUsernameLength = 128;

    /// <summary>
    /// 备注最大长度。
    /// </summary>
    public const int MaxDescriptionLength = 256;

    /// <summary>
    /// 别名（管理员可读的名字，唯一且不区分大小写）。
    /// </summary>
    /// <remarks>
    /// 允许任意可打印字符（含中文）——它只是给人看的，不进 URL 也不进 JWT。
    /// 但控制字符一律拒绝：它们会污染日志与界面。
    /// </remarks>
    public string Name { get; private set; } = String.Empty;

    /// <summary>
    /// 规范化后的服务地址（<c>host</c> 或 <c>host:port</c>）。
    /// </summary>
    public string ServerAddress { get; private set; } = String.Empty;

    /// <summary>
    /// 登录账号；为空表示匿名访问。
    /// </summary>
    public string Username { get; private set; } = String.Empty;

    /// <summary>
    /// 口令密文（Base64 文本）；为空表示该配置不带口令。
    /// </summary>
    /// <remarks>
    /// <b>是密文，不是明文</b>。解密只发生在「真的要拿凭据去连 Docker」的那一刻，
    /// 且解密结果不落到任何日志、响应或异常消息里。
    /// </remarks>
    public string EncryptedPassword { get; private set; } = String.Empty;

    /// <summary>
    /// 备注；可为空。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 该配置是否带凭据。
    /// </summary>
    /// <remarks>
    /// 匿名仓库也允许登记（记录地址与别名，便于统一管理），此时
    /// <see cref="Username"/> 与 <see cref="EncryptedPassword"/> 均为空。
    /// </remarks>
    public bool HasCredentials => this.Username.Length > 0;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Register"/>。
    /// </summary>
    private RegistryCredential()
    {
    }

    /// <summary>
    /// 登记一条 Registry 配置。
    /// </summary>
    /// <param name="id">主键。</param>
    /// <param name="name">别名；去首尾空白，长度 1-64，不得含控制字符。</param>
    /// <param name="serverAddress">服务地址；会被规范化为 <c>host</c> 或 <c>host:port</c>。</param>
    /// <param name="username">登录账号；为空或纯空白表示匿名访问。</param>
    /// <param name="encryptedPassword">口令<b>密文</b>；为空表示不带口令。</param>
    /// <param name="description">备注；可为空。</param>
    /// <returns>新建的配置实体。</returns>
    /// <exception cref="ArgumentException">别名或地址非法。</exception>
    public static RegistryCredential Register(
        Guid id,
        string name,
        string serverAddress,
        string? username = null,
        string? encryptedPassword = null,
        string? description = null)
    {
        var address = RegistryServerAddress.Parse(serverAddress);
        var normalizedUser = NormalizeOptional(username, MaxUsernameLength, nameof(username)) ?? String.Empty;

        // 有账号就必须有口令，反之亦然——半截凭据推镜像时只会得到「认证失败」，
        // 而看不出是配置缺了一半。
        if (normalizedUser.Length > 0 && String.IsNullOrEmpty(encryptedPassword))
        {
            throw new ArgumentException("填写了账号就必须提供口令", nameof(encryptedPassword));
        }

        if (normalizedUser.Length == 0 && !String.IsNullOrEmpty(encryptedPassword))
        {
            throw new ArgumentException("提供了口令就必须填写账号", nameof(username));
        }

        return new RegistryCredential
        {
            Id = id,
            Name = NormalizeName(name),
            ServerAddress = address.Value,
            Username = normalizedUser,
            EncryptedPassword = encryptedPassword ?? String.Empty,
            Description = NormalizeOptional(description, MaxDescriptionLength, nameof(description)),
        };
    }

    /// <summary>
    /// 规范化别名：去首尾空白、校验长度、拒绝控制字符。
    /// </summary>
    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var value = name.Trim();

        if (value.Length < MinNameLength || value.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Registry 别名长度必须在 {MinNameLength}-{MaxNameLength} 个字符之间",
                nameof(name));
        }

        foreach (var ch in value)
        {
            if (Char.IsControl(ch))
            {
                throw new ArgumentException("Registry 别名不得包含控制字符", nameof(name));
            }
        }

        return value;
    }

    /// <summary>
    /// 规范化可选文本：去首尾空白；为空（或全是空白）则返回 <see langword="null"/>。
    /// </summary>
    private static string? NormalizeOptional(string? text, int maxLength, string parameterName)
    {
        if (text is null)
        {
            return null;
        }

        var value = text.Trim();

        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"长度不得超过 {maxLength} 个字符", parameterName);
        }

        return value;
    }
}
