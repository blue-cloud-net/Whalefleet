using Docker.Manager.Application.Abstractions;
using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// Docker 端点规范化器：把五花八门的端点写法收敛成<b>唯一的规范连接串</b>。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：客户端缓存的键是连接串。若不规范化，
/// <c>/var/run/docker.sock</c>、<c>unix:///var/run/docker.sock</c>、<c>unix://var/run/docker.sock</c>
/// 会被当成三条不同的连接，各自建一整套客户端与 HTTP 连接池——「切换主机不重建连接」
/// 也就无从谈起。</para>
/// <para><b>规范形式</b>（右侧为 <see cref="NormalizedDockerEndpoint.CanonicalEndpoint"/>）：</para>
/// <list type="table">
///   <listheader>
///     <term>输入</term>
///     <description>规范形式</description>
///   </listheader>
///   <item>
///     <term><c>/var/run/docker.sock</c>、<c>unix:///var/run/docker.sock</c>、<c>unix://var/run/docker.sock</c></term>
///     <description><c>unix:///var/run/docker.sock</c></description>
///   </item>
///   <item>
///     <term><c>//./pipe/docker_engine</c>、<c>\\.\pipe\docker_engine</c>、<c>npipe://./pipe/docker_engine</c></term>
///     <description><c>npipe://./pipe/docker_engine</c></description>
///   </item>
///   <item>
///     <term><c>tcp://HOST:2375</c>、<c>host:2375</c>、<c>http://host</c></term>
///     <description><c>tcp://host:2375</c></description>
///   </item>
///   <item>
///     <term><c>https://HOST:2376</c>、<c>https://host</c>（TLS）</term>
///     <description><c>https://host:2376</c></description>
///   </item>
/// </list>
/// <para><b>TLS 为什么用 <c>https://</c> 而不是另加一个开关</b>：库按端点 scheme 选传输，
/// 且客户端证书只会在 TLS 通道上被使用。因此「端点写成 https」本身就是「这条连接要 TLS」的
/// 唯一声明，不会出现「配了证书却走了明文」这种静默降级。默认端口取 2376
/// （与 <c>docker --tlsverify</c> 的约定一致），区别于明文的 2375。</para>
/// <para><b>关于三个斜杠</b>：<c>unix://var/run/docker.sock</c> 会被 <see cref="Uri"/> 解析成
/// <c>Host="var"</c> + <c>AbsolutePath="/run/docker.sock"</c>，与直觉相悖。实测两种写法都能连通
/// Docker 守护进程，但只有三斜杠形式的 <c>AbsolutePath</c> 才是完整 socket 路径，
/// 因此统一纠正为路径写法，并在测试里断言 <c>AbsolutePath</c>。</para>
/// <para><b>幂等性</b>：<c>TryNormalize(TryNormalize(x).CanonicalEndpoint)</c> 必须等于
/// <c>TryNormalize(x)</c>，由 <c>DockerEndpointNormalizerTests</c> 守住。</para>
/// </remarks>
public static class DockerEndpointNormalizer
{
    /// <summary>
    /// TCP 端点在未显式给出端口时使用的 Docker 默认明文端口。
    /// </summary>
    public const int DefaultTcpPort = 2375;

    /// <summary>
    /// TLS 端点（<c>https://</c>）在未显式给出端口时使用的 Docker 默认加密端口。
    /// </summary>
    public const int DefaultTlsPort = 2376;

    private const string _unixScheme = "unix://";
    private const string _npipeScheme = "npipe://";
    private const string _npipePrefix = "npipe://./pipe/";
    private const string _npipeWindowsPrefix = @"\\.\pipe\";
    private const string _npipeSlashPrefix = "//./pipe/";
    private const string _tcpScheme = "tcp://";
    private const string _httpsScheme = "https://";
    private const string _schemeSeparator = "://";

    /// <summary>
    /// 尝试把端点规范化。
    /// </summary>
    /// <param name="type">连接方式。</param>
    /// <param name="endpoint">端点原文（可含 <c>unix://</c> / <c>npipe://</c> / <c>tcp://</c> 前缀）。</param>
    /// <param name="normalized">规范化结果（成功时有效）。</param>
    /// <param name="error">失败原因（成功时为 <c>null</c>）；可直接用于配置校验消息。</param>
    /// <returns>规范化成功返回 <c>true</c>。</returns>
    public static bool TryNormalize(
        DockerHostType type,
        string? endpoint,
        out NormalizedDockerEndpoint normalized,
        out string? error)
    {
        normalized = default;
        error = null;

        if (String.IsNullOrWhiteSpace(endpoint))
        {
            return Fail("端点不能为空", out normalized, out error);
        }

        var raw = endpoint.Trim();

        return type switch
        {
            DockerHostType.Socket => TryNormalizeSocket(raw, out normalized, out error),
            DockerHostType.Tcp => TryNormalizeTcp(raw, out normalized, out error),
            _ => Fail($"不支持的连接方式：{(int)type}", out normalized, out error),
        };
    }

    private static bool TryNormalizeSocket(
        string raw,
        out NormalizedDockerEndpoint normalized,
        out string? error)
    {
        normalized = default;
        error = null;

        if (IsNamedPipe(raw))
        {
            return TryNormalizeNamedPipe(raw, out normalized, out error);
        }

        var hasUnixScheme = raw.StartsWith(_unixScheme, StringComparison.OrdinalIgnoreCase);
        if (!hasUnixScheme && raw.Contains(_schemeSeparator, StringComparison.Ordinal))
        {
            return Fail($"Socket 端点不支持该协议前缀：{raw}", out normalized, out error);
        }

        var path = hasUnixScheme ? raw[_unixScheme.Length..] : raw;

        // 容忍 unix://var/run/docker.sock 的 authority 写法，纠正为路径语义（见类备注）。
        if (hasUnixScheme && !path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (!path.StartsWith('/'))
        {
            return Fail("Socket 端点必须是绝对路径（如 /var/run/docker.sock）", out normalized, out error);
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return Fail("Socket 端点不能包含反斜杠（命名管道请用 npipe://./pipe/<name>）", out normalized, out error);
        }

        while (path.StartsWith("//", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        path = path.TrimEnd('/');

        if (path.Length <= 1)
        {
            return Fail("Socket 端点缺少 socket 文件路径", out normalized, out error);
        }

        var canonical = _unixScheme + path;

        return Uri.TryCreate(canonical, UriKind.Absolute, out var uri)
            ? Succeed(uri, canonical, out normalized, out error)
            : Fail($"Socket 端点无法解析：{raw}", out normalized, out error);
    }

    private static bool TryNormalizeNamedPipe(
        string raw,
        out NormalizedDockerEndpoint normalized,
        out string? error)
    {
        normalized = default;
        error = null;

        var trimmed = raw.TrimEnd('/');
        var separatorIndex = trimmed.LastIndexOfAny(['/', '\\']);

        if (separatorIndex < 0)
        {
            return Fail("命名管道端点缺少管道名（如 npipe://./pipe/docker_engine）", out normalized, out error);
        }

        var name = trimmed[(separatorIndex + 1)..];
        if (name.Length == 0)
        {
            return Fail("命名管道端点缺少管道名（如 npipe://./pipe/docker_engine）", out normalized, out error);
        }

        var canonical = _npipePrefix + name;

        return Uri.TryCreate(canonical, UriKind.Absolute, out var uri)
            ? Succeed(uri, canonical, out normalized, out error)
            : Fail($"命名管道端点无法解析：{raw}", out normalized, out error);
    }

    private static bool TryNormalizeTcp(
        string raw,
        out NormalizedDockerEndpoint normalized,
        out string? error)
    {
        normalized = default;
        error = null;

        var remainder = raw;
        var separatorIndex = raw.IndexOf(_schemeSeparator, StringComparison.Ordinal);
        var isTls = false;

        if (separatorIndex > 0)
        {
            var scheme = raw[..separatorIndex];

            if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                isTls = true;
            }
            else if (!scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"TCP 端点不支持该协议前缀：{scheme}://", out normalized, out error);
            }

            remainder = raw[(separatorIndex + _schemeSeparator.Length)..];
        }

        if (remainder.Contains('@', StringComparison.Ordinal))
        {
            return Fail(
                "TCP 端点不支持内嵌账号密码（端点会进日志），Basic 凭据请放到认证配置里",
                out normalized,
                out error);
        }

        var slashIndex = remainder.IndexOf('/', StringComparison.Ordinal);
        var authority = slashIndex >= 0 ? remainder[..slashIndex] : remainder;
        var path = slashIndex >= 0 ? remainder[slashIndex..].TrimEnd('/') : String.Empty;

        if (authority.Length == 0)
        {
            return Fail(
                "TCP 端点缺少主机名（如 tcp://host:2375 或 https://host:2376）",
                out normalized,
                out error);
        }

        var defaultPort = isTls ? DefaultTlsPort : DefaultTcpPort;

        if (!TrySplitAuthority(authority, defaultPort, out var host, out var port, out error))
        {
            normalized = default;
            return false;
        }

        if (port is < 1 or > 65535)
        {
            return Fail($"TCP 端点端口超出范围（1-65535）：{port}", out normalized, out error);
        }

        var canonical = $"{(isTls ? _httpsScheme : _tcpScheme)}{host.ToLowerInvariant()}:{port}{path}";

        return Uri.TryCreate(canonical, UriKind.Absolute, out var uri)
            ? Succeed(uri, canonical, out normalized, out error)
            : Fail($"TCP 端点无法解析：{raw}", out normalized, out error);
    }

    private static bool TrySplitAuthority(
        string authority,
        int defaultPort,
        out string host,
        out int port,
        out string? error)
    {
        host = String.Empty;
        port = defaultPort;
        error = null;

        if (authority.StartsWith('['))
        {
            var closingBracket = authority.IndexOf(']', StringComparison.Ordinal);
            if (closingBracket < 0)
            {
                error = "TCP 端点的 IPv6 地址缺少右方括号";
                return false;
            }

            host = authority[..(closingBracket + 1)];
            var tail = authority[(closingBracket + 1)..];

            if (tail.Length == 0)
            {
                return true;
            }

            if (!tail.StartsWith(':') || !Int32.TryParse(tail[1..], out port))
            {
                error = "TCP 端点的端口非法";
                return false;
            }

            return true;
        }

        // 用 Span 重载：string.LastIndexOf(char) 会触发 CA1307（缺少显式比较语义），
        // 而 string.LastIndexOf(char, StringComparison) 并不存在（与 IndexOf 不对称）。
        var colonIndex = authority.AsSpan().LastIndexOf(':');
        if (colonIndex < 0)
        {
            host = authority;
            return true;
        }

        if (!Int32.TryParse(authority[(colonIndex + 1)..], out port))
        {
            error = "TCP 端点的端口非法";
            return false;
        }

        host = authority[..colonIndex];

        if (host.Length == 0)
        {
            error = "TCP 端点缺少主机名（如 tcp://host:2375 或 https://host:2376）";
            return false;
        }

        return true;
    }

    private static bool IsNamedPipe(string raw) =>
        raw.StartsWith(_npipeScheme, StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith(_npipeSlashPrefix, StringComparison.Ordinal)
        || raw.StartsWith(_npipeWindowsPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool Succeed(
        Uri uri,
        string canonical,
        out NormalizedDockerEndpoint normalized,
        out string? error)
    {
        normalized = new NormalizedDockerEndpoint(uri, canonical);
        error = null;
        return true;
    }

    private static bool Fail(
        string message,
        out NormalizedDockerEndpoint normalized,
        out string? error)
    {
        normalized = default;
        error = message;
        return false;
    }
}
