namespace Docker.Manager.Core.Hosts;

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

    /// <summary>
    /// 返回端点原文，便于日志与调试输出。
    /// </summary>
    public override string ToString() => this.Value;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return this.Value;
    }
}
