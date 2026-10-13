namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// TLS（双向认证）凭据：客户端证书 + 私钥，以及用于校验服务端证书的 CA。
/// </summary>
/// <remarks>
/// <para><b>为什么三个文件都是必填</b>：Docker 守护进程默认用自己的 CA 签发的自签证书，
/// 若不给 CA 就只能接受任意服务端证书（等于放弃服务端身份校验，可被中间人冒充）；
/// 若不给客户端证书，则守护进程会拒绝连接（<c>--tlsverify</c> 要求双向）。
/// 因此「三个都给」才是唯一安全的组合，缺任何一个都直接报错而不是降级。</para>
/// <para><b>为何只支持 PEM</b>：与 <c>docker --tlsverify --tlscacert/--tlscert/--tlskey</c>
/// 的约定一致——运维手里本来就是这三件套，不用额外转换格式。</para>
/// <para><b>路径在<b>连接时</b>才读取</b>：这里只校验字段形态，不做文件系统检查。
/// 证书可能由编排系统在容器启动后才挂载进来，构造连接描述时不存在不代表配置错误；
/// 真正读取发生在构建客户端时（每台主机只发生一次）。</para>
/// <para><b><see cref="ToString"/> 不输出口令</b>：记录类型默认的 <c>ToString</c> 会把所有属性
/// 拼进字符串，一旦被误打进日志就是明文泄漏，因此必须覆写。</para>
/// </remarks>
public sealed record DockerTlsCredentials
{
    /// <summary>
    /// CA 证书文件路径（PEM）。
    /// </summary>
    public required string CaCertificatePath { get; init; }

    /// <summary>
    /// 客户端证书文件路径（PEM）。
    /// </summary>
    public required string ClientCertificatePath { get; init; }

    /// <summary>
    /// 客户端私钥文件路径（PEM）。
    /// </summary>
    public required string ClientKeyPath { get; init; }

    /// <summary>
    /// 私钥口令；私钥未加密时为 <see langword="null"/>。
    /// </summary>
    public string? ClientKeyPassword { get; init; }

    /// <summary>
    /// 从凭据载荷 JSON 解析（字段见 <c>docs/architecture.md</c> 的「主机凭据」小节）。
    /// </summary>
    /// <param name="configJson">凭据载荷 JSON。</param>
    /// <returns>TLS 凭据。</returns>
    /// <exception cref="Common.BusinessException">载荷非法或缺少必填字段（错误码 <c>1301</c>）。</exception>
    public static DockerTlsCredentials Parse(string? configJson)
    {
        var payload = CredentialsJson.Deserialize<Payload>(configJson, Description);

        return new DockerTlsCredentials
        {
            CaCertificatePath = CredentialsJson.RequireText(
                payload.CaCertificatePath,
                nameof(Payload.CaCertificatePath),
                Description),
            ClientCertificatePath = CredentialsJson.RequireText(
                payload.ClientCertificatePath,
                nameof(Payload.ClientCertificatePath),
                Description),
            ClientKeyPath = CredentialsJson.RequireText(
                payload.ClientKeyPath,
                nameof(Payload.ClientKeyPath),
                Description),
            ClientKeyPassword = payload.ClientKeyPassword,
        };
    }

    /// <summary>
    /// 只输出路径与「是否带口令」，<b>绝不</b>输出口令原文。
    /// </summary>
    /// <returns>不含敏感内容的描述。</returns>
    public override string ToString() =>
        $"DockerTlsCredentials(ca={this.CaCertificatePath}, cert={this.ClientCertificatePath}, "
        + $"key={this.ClientKeyPath}, password={(this.ClientKeyPassword is null ? "无" : "有")})";

    private const string Description = "TLS";

    /// <summary>载荷 DTO：字段可空，由 <see cref="Parse"/> 逐个校验并给出可读错误。</summary>
    private sealed record Payload
    {
        public string? CaCertificatePath { get; init; }

        public string? ClientCertificatePath { get; init; }

        public string? ClientKeyPath { get; init; }

        public string? ClientKeyPassword { get; init; }
    }
}
