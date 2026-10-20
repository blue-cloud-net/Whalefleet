using Whalefleet.Core.Common;

namespace Whalefleet.Core.Repositories;

/// <summary>
/// 私有仓库 的服务地址（<c>host</c> 或 <c>host:port</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么是值对象而不是字符串</b>：这个地址会出现在三个地方——存库的
/// <c>server_address</c> 列、推镜像时拼出的镜像引用（<c>registry.example.com/app:v1</c>）、
/// 以及 Docker 认证配置里的 <c>ServerAddress</c>。三处必须<b>逐字一致</b>，
/// 否则守护进程会拿「A 处的地址」去请求「B 处的仓库」，报出来的是权限错误而不是地址错误。
/// 一旦规范化只发生在一个地方，这三处就不可能漂移。</para>
/// <para><b>规范化做什么</b>：去掉协议前缀（<c>https://</c>）、去掉路径（<c>/v2/</c> 之类）、
/// 去掉端口号末尾的空白、统一转小写。<b>刻意不</b>补协议、不补 <c>/v2/</c>——
/// Docker 的 <c>AuthConfig.ServerAddress</c> 要的就是裸的 <c>host:port</c>。</para>
/// <para><b>为什么不做 DNS 解析或连通性检查</b>：那是「测试连通性」用例的职责（<c>RepositoryService.Test</c>），
/// 而地址格式是否合法是<b>不变量</b>，属于实体自身。</para>
/// </remarks>
public sealed class RepositoryServerAddress : ValueObject
{
    /// <summary>
    /// 规范化后地址的最大长度。
    /// </summary>
    public const int MaxLength = 255;

    /// <summary>
    /// 初始化地址值对象。
    /// </summary>
    /// <param name="value">规范化后的地址文本（<c>host</c> 或 <c>host:port</c>）。</param>
    /// <param name="host">主机名或 IP（IPv6 已去掉方括号）。</param>
    /// <param name="port">端口号；未指定时为 <see langword="null"/>（走默认 443 / 80）。</param>
    private RepositoryServerAddress(string value, string host, int? port)
    {
        this.Value = value;
        this.Host = host;
        this.Port = port;
    }

    /// <summary>
    /// 规范化后的地址文本（<c>host</c> 或 <c>host:port</c>）。
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 主机名或 IP 字面量（IPv6 已去掉方括号）。
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// 端口号；未显式指定时为 <see langword="null"/>。
    /// </summary>
    public int? Port { get; }

    /// <summary>
    /// 解析并规范化一个仓库地址。
    /// </summary>
    /// <param name="address">原始地址（可带 <c>https://</c> 前缀、路径与端口）。</param>
    /// <returns>规范化后的地址值对象。</returns>
    /// <exception cref="ArgumentException">地址为空、格式非法或超长。</exception>
    public static RepositoryServerAddress Parse(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var value = address.Trim();

        // 去掉协议前缀：http:// 与 https:// 对 仓库地址没有意义——
        // Docker 客户端自己决定走哪条路，地址里带了协议反而会被当成主机名的一部分。
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            value = value[(schemeIndex + 3)..];
        }

        // 去掉路径：/v2/ 之类的后缀不是地址的一部分。
        var slashIndex = value.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            value = value[..slashIndex];
        }

        value = value.Trim().ToLowerInvariant();

        if (value.Length == 0)
        {
            throw new ArgumentException("仓库地址不得为空", nameof(address));
        }

        if (value.Length > MaxLength)
        {
            throw new ArgumentException(
                $"仓库地址长度不得超过 {MaxLength} 个字符",
                nameof(address));
        }

        string host;
        int? port = null;

        if (value.StartsWith('['))
        {
            // 括号包裹的 IPv6：[::1] 或 [::1]:5000
            var closeIndex = value.IndexOf(']', StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                throw new ArgumentException("仓库地址的 IPv6 字面量缺少右方括号", nameof(address));
            }

            host = value[1..closeIndex];
            var remainder = value[(closeIndex + 1)..];

            if (remainder.Length > 0)
            {
                if (remainder[0] != ':')
                {
                    throw new ArgumentException("仓库地址的 IPv6 字面量后只允许接「:端口」", nameof(address));
                }

                port = ParsePort(remainder[1..], address);
            }
        }
        else
        {
            // 多个冒号 = 未加括号的 IPv6（如 ::1）。Docker 那边也要求 IPv6 必须加括号。
            // 判据是「冒号数量」而不是「最后一个冒号之后还有没有」：
            // 后者在 LastIndexOf 的结果上恒为假，写出来既不能表达意图也会误导读者。
            if (CountChar(value, ':') > 1)
            {
                throw new ArgumentException("IPv6 地址必须用方括号括起，例如 [::1]:5000", nameof(address));
            }

            // 用 char 重载：它没有接受 StringComparison 的版本（CA1307 对 char 重载不适用），
            // 而字符串重载会被 CA1865 提示改回 char。
            var colonIndex = value.LastIndexOf(':');

            if (colonIndex >= 0)
            {
                host = value[..colonIndex];
                port = ParsePort(value[(colonIndex + 1)..], address);
            }
            else
            {
                host = value;
            }
        }

        EnsureValidHost(host, address);

        return new RepositoryServerAddress(value, host, port);
    }

    /// <summary>
    /// 尝试解析地址，不抛异常。
    /// </summary>
    /// <param name="address">原始地址。</param>
    /// <param name="result">解析成功时为规范化后的地址，失败时为 <see langword="null"/>。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    public static bool TryParse(string? address, out RepositoryServerAddress? result)
    {
        if (String.IsNullOrWhiteSpace(address))
        {
            result = null;
            return false;
        }

        try
        {
            result = Parse(address);
            return true;
        }
        catch (ArgumentException)
        {
            result = null;
            return false;
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return this.Value;
    }

    /// <inheritdoc />
    public override string ToString() => this.Value;

    /// <summary>
    /// 统计文本里某个字符出现的次数（避开 <c>string.IndexOf(char)</c> 的 CA1307 告警）。
    /// </summary>
    /// <param name="text">待统计的文本。</param>
    /// <param name="target">目标字符。</param>
    /// <returns>出现次数。</returns>
    private static int CountChar(string text, char target)
    {
        var count = 0;

        foreach (var ch in text)
        {
            if (ch == target)
            {
                count++;
            }
        }

        return count;
    }

    private static int ParsePort(string text, string address)
    {
        if (text.Length == 0)
        {
            throw new ArgumentException("仓库地址的端口号不得为空", nameof(address));
        }

        if (!Int32.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"仓库地址的端口号非法：「{text}」（应为 1-65535）",
                nameof(address));
        }

        return port;
    }

    private static void EnsureValidHost(string host, string address)
    {
        if (host.Length == 0)
        {
            throw new ArgumentException("仓库地址缺少主机名", nameof(address));
        }

        // IPv6 字面量不走主机名规则：它含冒号与十六进制数字，
        // 而 DNS 主机名的字符集根本不包含冒号。
        if (host.Contains(':', StringComparison.Ordinal))
        {
            EnsureValidIpv6(host, address);
            return;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length == 0)
            {
                throw new ArgumentException("仓库地址的主机名含有空的标签", nameof(address));
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                throw new ArgumentException("仓库地址的主机名标签不得以「-」开头或结尾", nameof(address));
            }

            foreach (var ch in label)
            {
                var isLetterOrDigit = ch is >= 'a' and <= 'z' or >= '0' and <= '9';

                if (!isLetterOrDigit && ch != '-')
                {
                    throw new ArgumentException(
                        $"仓库地址的主机名含有非法字符：「{ch}」",
                        nameof(address));
                }
            }
        }
    }

    /// <summary>
    /// 校验 IPv6 字面量：只允许十六进制数字、冒号与点（IPv4 映射写法）。
    /// </summary>
    /// <remarks>
    /// <b>刻意不做完整的 IPv6 语法校验</b>（分组数、每组位数等）：
    /// 那是 <c>System.Net.IPAddress.TryParse</c> 的职责，而它对某些
    /// 压缩写法（如 <c>::ffff:192.0.2.1</c>）与 Docker 的接受范围并不完全一致。
    /// 这里只挡下「明显不是地址」的东西（如 <c>gg::1</c>），
    /// 真正的连通性由探测用例回答。
    /// </remarks>
    private static void EnsureValidIpv6(string host, string address)
    {
        foreach (var ch in host)
        {
            var isHexDigit = ch is >= '0' and <= '9' or >= 'a' and <= 'f';
            var isAllowed = isHexDigit || ch is ':' or '.';

            if (!isAllowed)
            {
                throw new ArgumentException(
                    $"仓库地址的 IPv6 字面量含有非法字符：「{ch}」",
                    nameof(address));
            }
        }
    }
}
