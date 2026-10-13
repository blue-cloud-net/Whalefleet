namespace Docker.Manager.Application.Hosts;

/// <summary>
/// 主机管理端点的请求契约（创建 / 更新 / 测试）。
/// </summary>
/// <remarks>
/// <para><b>字段一律可空</b>：请求体由客户端决定，缺字段是常态而非异常。若声明为非空类型，
/// 「没传 endpoint」会表现为空引用异常（500），而正确语义是参数校验失败（1000）。
/// 校验统一在 <see cref="HostService"/> 里做，消息也由那里给出。</para>
/// <para><b><c>type</c> 与 <c>enabled</c> 为何是字符串 / 可空布尔</b>：<c>type</c> 用字符串
/// （<c>"socket"</c> / <c>"tcp"</c>）是因为枚举值由客户端文本给出，无法直接绑定成枚举而不暴露数字；
/// <c>enabled</c> 可空是为了区分「未提供」与「显式设为 false」——更新时前者表示不改。</para>
/// </remarks>
public sealed record CreateHostRequest
{
    /// <summary>主机名（对外标识，创建后不可改）。</summary>
    public string? Name { get; init; }

    /// <summary>连接方式：<c>socket</c> 或 <c>tcp</c>。</summary>
    public string? Type { get; init; }

    /// <summary>端点，如 <c>/var/run/docker.sock</c>、<c>tcp://host:2375</c>、<c>https://host:2376</c>。</summary>
    public string? Endpoint { get; init; }

    /// <summary>TLS 凭据；与 <see cref="BasicAuth"/> 互斥。</summary>
    public TlsConfigInput? TlsConfig { get; init; }

    /// <summary>Basic 凭据；与 <see cref="TlsConfig"/> 互斥。</summary>
    public BasicAuthInput? BasicAuth { get; init; }
}

/// <summary>
/// 更新主机的请求契约。
/// </summary>
/// <remarks>
/// <b>不含 <c>name</c></b>：主机名是对外标识（路径里的 <c>hostId</c> 就是它），改名会让已发放的
/// 深链与书签失效，因此只允许「新建时定名」。
/// </remarks>
public sealed record UpdateHostRequest
{
    /// <summary>新的端点；未提供表示不改。</summary>
    public string? Endpoint { get; init; }

    /// <summary>新的 TLS 凭据；未提供表示不改。</summary>
    public TlsConfigInput? TlsConfig { get; init; }

    /// <summary>新的 Basic 凭据；未提供表示不改。</summary>
    public BasicAuthInput? BasicAuth { get; init; }

    /// <summary>启用 / 停用；未提供表示不改。</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// 清空认证信息（改为无认证）。
    /// </summary>
    /// <remarks>
    /// 需要这个显式开关的原因：JSON 无法区分「没给 <c>tlsConfig</c>」与「<c>tlsConfig: null</c>」，
    /// 于是「把主机从 TLS 改回明文」就没有表达方式——而 TLS 凭据留在明文端点上会被直接拒绝（1301），
    /// 变成用户无法自救的配置。与 <see cref="TlsConfig"/> / <see cref="BasicAuth"/> 互斥。
    /// </remarks>
    public bool? ClearCredentials { get; init; }
}

/// <summary>
/// 测试未保存配置的请求契约（对应 <c>POST /api/v1/admin/hosts/test</c>）。
/// </summary>
/// <remarks>刻意不带 <c>hostId</c>：测的是「还没保存的那份配置」。</remarks>
public sealed record TestHostRequest
{
    /// <summary>连接方式：<c>socket</c> 或 <c>tcp</c>。</summary>
    public string? Type { get; init; }

    /// <summary>端点。</summary>
    public string? Endpoint { get; init; }

    /// <summary>TLS 凭据；与 <see cref="BasicAuth"/> 互斥。</summary>
    public TlsConfigInput? TlsConfig { get; init; }

    /// <summary>Basic 凭据；与 <see cref="TlsConfig"/> 互斥。</summary>
    public BasicAuthInput? BasicAuth { get; init; }
}

/// <summary>
/// TLS 凭据输入（三个路径 + 可选口令），对应 <c>auth_config_json</c> 的 <c>tls</c> 形态。
/// </summary>
/// <remarks>
/// 与 <c>DockerTlsCredentials</c>（运行期凭据）分开是有意的：这里允许字段为空，
/// 以便校验时能指出**具体缺哪个字段**；运行期那个类型用 <c>required</c> 保证「拿到的必然完整」。
/// </remarks>
public sealed record TlsConfigInput
{
    /// <summary>CA 证书路径（PEM）。</summary>
    public string? CaCertificatePath { get; init; }

    /// <summary>客户端证书路径（PEM）。</summary>
    public string? ClientCertificatePath { get; init; }

    /// <summary>客户端私钥路径（PEM）。</summary>
    public string? ClientKeyPath { get; init; }

    /// <summary>私钥口令；未加密时为 <see langword="null"/>。</summary>
    public string? ClientKeyPassword { get; init; }
}

/// <summary>
/// Basic 凭据输入，对应 <c>auth_config_json</c> 的 <c>basic</c> 形态。
/// </summary>
public sealed record BasicAuthInput
{
    /// <summary>用户名。</summary>
    public string? Username { get; init; }

    /// <summary>密码。</summary>
    public string? Password { get; init; }
}
