using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Docker;

/// <summary>
/// 数据卷与网络共用的<b>入参形状</b>校验（名字、标签、IP 参数）。
/// </summary>
/// <remarks>
/// <para><b>为什么要在服务端先挡一道</b>：这些规则守护进程全都强制执行，
/// 但它报回来的方式是 <c>400 Bad Request</c> + 一句英文，
/// 经 <c>DockerErrorTranslator</c> 归成 <c>1402</c>/<b>500</b>。
/// 也就是说「用户把卷名打错了一个字符」会变成一次「服务端故障」，
/// 而界面完全无法把提示落到对应输入框上。在本层挡下，同样的输入错误变成
/// <c>1000</c>/400，且消息能说清到底哪一条不合法。</para>
/// <para><b>规则只抄必要的部分</b>：Docker 的名字规则是
/// <c>^[a-zA-Z0-9][a-zA-Z0-9_.-]*$</c>（非 Swarm 作用域下）。
/// 刻意<b>不</b>加长度上限与「必须包含点号」之类的额外约束——
/// 那些规则存在于 CLI 的命名建议里，守护进程并不强制，加上去只会拒绝合法的名字。</para>
/// <para><b>类型</b>是 <c>internal</c>：这些校验服务于入参形状，不是业务概念，
/// 不需要进入对外契约，因此不进 <c>Application</c> 的公共面。</para>
/// </remarks>
internal static partial class DockerResourceName
{
    /// <summary>空标签的共享实例，避免每次调用都分配一个新字典。</summary>
    private static readonly Dictionary<string, string> EmptyLabels = new(StringComparer.Ordinal);

    /// <summary>数据卷与网络的合法名字：字母或数字开头，其后是字母、数字、<c>_</c>、<c>.</c>、<c>-</c>。</summary>
    /// <remarks>
    /// 与守护进程的非 Swarm 分支保持一致。注意末尾是 <c>*</c> 而非 <c>+</c>：
    /// Docker 校验表达式里用的是 <c>+</c>（要求至少 2 个字符），但单字符名字在守护进程上确实能建出来，
    /// 按 <c>*</c> 校验不会把合法输入挡在门外，而按 <c>+</c> 有可能把合法的挡掉。
    /// </remarks>
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidNamePattern();

    /// <summary>
    /// 校验并归一化一个数据卷 / 网络名。
    /// </summary>
    /// <param name="name">原始名字。</param>
    /// <param name="field">字段名（用于拼错误消息，如 <c>name</c> / <c>networkId</c>）。</param>
    /// <param name="kind">资源中文名（「数据卷」/「网络」），用于拼错误消息。</param>
    /// <returns>去掉首尾空白后的名字。</returns>
    /// <exception cref="BusinessException">名字为空或含非法字符（<c>1000</c>）。</exception>
    internal static string Require(string? name, string field, string kind)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        if (!ValidNamePattern().IsMatch(trimmed))
        {
            throw BusinessException.Validation(
                $"{kind}名「{trimmed}」不合法：须以字母或数字开头，其余只能是字母、数字、下划线、点与连字符");
        }

        return trimmed;
    }

    /// <summary>
    /// 归一化标签字典（去首尾空白、丢弃空键）。
    /// </summary>
    /// <param name="labels">原始标签。</param>
    /// <returns>可直接传给守护进程的标签字典；输入为空或全为空键时返回共享的空字典。</returns>
    /// <remarks>
    /// <b>为什么丢弃空键而不是报错</b>：空键说明客户端的序列化方式有问题
    /// （通常是可选输入框没填就提交了 <c>{"": ""}</c>）。这类问题不该阻断整个操作，
    /// 而把空键原样发过去会让守护进程报一句无法定位的错误。
    /// 丢弃它得到的结果与用户本来的意图一致。
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> NormalizeLabels(
        IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is not { Count: > 0 })
        {
            return EmptyLabels;
        }

        var normalized = new Dictionary<string, string>(labels.Count, StringComparer.Ordinal);

        foreach (var (key, value) in labels)
        {
            var trimmedKey = key?.Trim();

            if (string.IsNullOrEmpty(trimmedKey))
            {
                continue;
            }

            normalized[trimmedKey] = value ?? string.Empty;
        }

        return normalized.Count > 0 ? normalized : EmptyLabels;
    }

    /// <summary>
    /// 校验并归一化一个 IP 地址或 CIDR 网段参数。
    /// </summary>
    /// <param name="value">原始值（如 <c>172.18.0.0/16</c>）。</param>
    /// <param name="field">字段名（用于拼错误消息）。</param>
    /// <param name="requirePrefixLength">是否要求带 <c>/前缀长度</c>（子网要求，地址不要求）。</param>
    /// <returns>去掉首尾空白后的值；为空时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">格式非法（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>为什么这里要自己解析</b>：<c>subnet</c> 写错时守护进程报的是 400，
    /// 经翻译层归成 1402/500——用户在自己填的网段上打错一个字符，却收到「服务端故障」。
    /// 这个检查只是一次 <c>IPAddress.TryParse</c>，成本可以忽略。
    /// </remarks>
    internal static string? NormalizeIpParameter(
        string? value,
        string field,
        bool requirePrefixLength)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);

        if (slash < 0)
        {
            if (requirePrefixLength)
            {
                throw BusinessException.Validation(
                    $"{field}「{trimmed}」必须是 CIDR 网段（如 172.18.0.0/16），缺少前缀长度");
            }

            return IPAddress.TryParse(trimmed, out _) ? trimmed : throw Invalid(field, trimmed);
        }

        var addressPart = trimmed[..slash];
        var prefixPart = trimmed[(slash + 1)..];

        if (!IPAddress.TryParse(addressPart, out var address) ||
            !Int32.TryParse(prefixPart, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            throw Invalid(field, trimmed);
        }

        // 前缀长度上限随地址族变化（IPv4 是 32，IPv6 是 128）。
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;

        if (prefix < 0 || prefix > maxPrefix)
        {
            throw BusinessException.Validation(
                $"{field}「{trimmed}」的前缀长度应在 0 到 {maxPrefix} 之间");
        }

        return trimmed;
    }

    /// <summary>统一的「IP 参数非法」异常。</summary>
    private static BusinessException Invalid(string field, string value) =>
        BusinessException.Validation($"{field}「{value}」不是合法的 IP 地址或 CIDR 网段");
}
