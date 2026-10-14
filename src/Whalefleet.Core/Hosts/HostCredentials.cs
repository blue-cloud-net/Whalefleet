namespace Whalefleet.Core.Hosts;

/// <summary>
/// Docker 主机的认证凭据（值对象）。
/// </summary>
/// <remarks>
/// <para><b>载荷是不透明的 JSON</b>：TLS 证书路径集合与 Basic 账号密码的结构由 0.0.8 落地时定义，
/// 本值对象只保证「认证方式与载荷的一致性」以及「载荷是合法 JSON」。
/// 避免过早把未定的字段固化成类型，也避免凭据结构变化就改动实体形状。</para>
/// <para><b>敏感信息</b>：<see cref="ConfigJson"/> 中的密码 / 私钥本身需要加密后落库
/// （0.0.8 / 0.4.0 的凭据加密），加密发生在应用/基础设施层；本值对象不承担加密职责，
/// 但<b>禁止</b>把明文凭据写进日志（<see cref="ToString"/> 只输出认证方式）。</para>
/// </remarks>
public sealed class HostCredentials : ValueObject
{
    /// <summary>
    /// 无认证（本地 socket 或明文 TCP）。
    /// </summary>
    public static HostCredentials None { get; } = new(DockerHostAuthKind.None, null);

    /// <summary>
    /// 认证方式。
    /// </summary>
    public DockerHostAuthKind Kind { get; }

    /// <summary>
    /// 认证载荷（合法 JSON）；<see cref="DockerHostAuthKind.None"/> 时为 <see langword="null"/>。
    /// </summary>
    public string? ConfigJson { get; }

    private HostCredentials(DockerHostAuthKind kind, string? configJson)
    {
        this.Kind = kind;
        this.ConfigJson = configJson;
    }

    /// <summary>
    /// 由认证方式与 JSON 载荷构造凭据。
    /// </summary>
    /// <param name="kind">认证方式。</param>
    /// <param name="configJson">
    /// 载荷。为 <see cref="DockerHostAuthKind.None"/> 时必须为空；
    /// 否则必须是合法 JSON 文本。
    /// </param>
    /// <returns>凭据值对象；无认证时返回共享的 <see cref="None"/> 实例。</returns>
    /// <exception cref="ArgumentException">认证方式未知、载荷与认证方式不匹配，或载荷不是合法 JSON。</exception>
    public static HostCredentials FromJson(DockerHostAuthKind kind, string? configJson)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"未知的认证方式：{(int)kind}", nameof(kind));
        }

        if (kind == DockerHostAuthKind.None)
        {
            if (!String.IsNullOrWhiteSpace(configJson))
            {
                throw new ArgumentException("无认证时不得携带凭据载荷", nameof(configJson));
            }

            return None;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);
        EnsureWellFormedJson(configJson);

        return new HostCredentials(kind, configJson);
    }

    /// <summary>
    /// 只输出认证方式，<b>绝不</b>输出载荷内容。
    /// </summary>
    public override string ToString() => $"HostCredentials({this.Kind})";

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return this.Kind;
        yield return this.ConfigJson;
    }

    private static void EnsureWellFormedJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("凭据载荷不是合法 JSON", nameof(json), exception);
        }
    }
}
