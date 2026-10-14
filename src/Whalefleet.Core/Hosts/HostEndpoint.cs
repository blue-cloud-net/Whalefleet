namespace Whalefleet.Core.Hosts;

/// <summary>
/// Docker 主机端点（值对象）。
/// </summary>
/// <remarks>
/// <para><b>只做粗粒度校验</b>：非空、无内嵌空白、长度不超限。端点与连接方式是否匹配
/// （socket 需绝对路径 / TCP 需主机端口）由 <c>DockerEndpointNormalizer</c>（Infrastructure）
/// 权威判定，并在 <see cref="DockerHost"/> 创建时做一次形态粗筛。
/// 刻意<b>不</b>在此重复那套判定规则——两处规则一旦漂移就会互相打架。</para>
/// <para><b>为什么是值对象</b>：端点没有身份，语义上按"字符串相等"使用；
/// 作为值对象参与相等性比较可避免调用方到处写字符串比较。</para>
/// </remarks>
public sealed class HostEndpoint : ValueObject
{
    /// <summary>
    /// 端点最大长度。
    /// </summary>
    public const int MaxLength = 512;

    /// <summary>
    /// 端点原文（已去首尾空白，未做连接串规范化）。
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 日志与调试用的**脱敏**形态。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里刻意与 <see cref="Value"/> 不同：<c>Value</c> 是要落库 / 发请求的原文，
    /// 而任何 <c>ToString()</c> 都可能被结构化日志（<c>{HostEndpoint}</c> 插值、
    /// <c>Destructure.ByTransforming</c>、<c>ILogger</c> 的对象插值）悄悄拿走。
    ///
    /// 端点是**凭据载体**：<c>tcp://admin:s3cr3t@10.0.0.5:2376</c> 这种写法在
    /// <c>DockerEndpointNormalizer</c> 里会被拒绝（它明确写着「端点会进日志」），
    /// 但那层在 Infrastructure、且只在**连接时**才跑得到——而
    /// <c>HostService.CreateAsync</c> 在此之前就已经把它写进日志与数据库了。
    ///
    /// 因此这里给出第二道防线：<c>ToString()</c> 永远不给 userinfo，
    /// 让「不小心把对象塞进日志」这件事不再等价于凭据泄漏。
    /// 真正要拿原文的地方请显式读 <see cref="Value"/>。
    /// </remarks>
    public override string ToString() => Redact(this.Value);

    /// <summary>
    /// 抹掉 URL 里的 userinfo（<c>scheme://user:secret@host</c> → <c>scheme://***@host</c>）。
    /// </summary>
    /// <remarks>
    /// 实现刻意保守：只处理含 <c>://</c> 的 URL 且只处理**第一个** <c>@</c> 之前的内容。
    /// 路径里出现 <c>@</c> 是合法的（如卷名 <c>data@home</c>），不能一刀切删掉整段。
    /// socket 路径（<c>/var/run/docker.sock</c>）不含 userinfo，原样返回。
    /// </remarks>
    public static string Redact(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return endpoint ?? string.Empty;
        }

        var schemeIndex = endpoint.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return endpoint;
        }

        var authorityStart = schemeIndex + _schemeSeparatorLength;

        // 只在 authority 段内找 @：路径 / 查询串里的 @ 不算
        var searchEnd = endpoint.IndexOfAny(PathOrQuerySeparators, authorityStart);
        if (searchEnd < 0)
        {
            searchEnd = endpoint.Length;
        }

        var at = endpoint.IndexOf('@', authorityStart, searchEnd - authorityStart);
        if (at < 0)
        {
            return endpoint;
        }

        return string.Concat(endpoint.AsSpan(0, authorityStart), "***@", endpoint.AsSpan(at + 1));
    }

    private const int _schemeSeparatorLength = 3;

    private static readonly char[] PathOrQuerySeparators = ['/', '?', '#'];

    /// <summary>
    /// 初始化端点。
    /// </summary>
    /// <param name="value">端点，如 <c>/var/run/docker.sock</c>、<c>unix:///var/run/docker.sock</c>、<c>tcp://host:2375</c>。</param>
    /// <exception cref="ArgumentException">为空、全为空白、长度超限或含内嵌空白字符。</exception>
    public HostEndpoint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"端点长度不得超过 {MaxLength} 个字符", nameof(value));
        }

        if (trimmed.Any(Char.IsWhiteSpace))
        {
            throw new ArgumentException("端点不得包含空白字符", nameof(value));
        }

        this.Value = trimmed;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return this.Value;
    }
}
