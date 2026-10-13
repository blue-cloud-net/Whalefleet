namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// HTTP Basic 凭据（用户名 + 密码）。
/// </summary>
/// <remarks>
/// <para><b>建议配合 TLS 使用</b>：Basic 凭据是<b>可逆</b>的（Base64 传输），
/// 明文 <c>tcp://</c> 上的 Basic 等于把密码交给任何能嗅探链路的人。
/// 因此明文场景会在构建客户端时记一条 Warning（不阻断——内网专线上确实有人这么用）。</para>
/// <para><b><see cref="ToString"/> 必须覆写</b>：记录类型默认实现会把密码原样拼进字符串，
/// 一旦被误打进日志就是明文泄漏。</para>
/// </remarks>
public sealed record DockerBasicCredentials
{
    /// <summary>
    /// 用户名。
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// 密码。
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// 从凭据载荷 JSON 解析（字段见 <c>docs/architecture.md</c> 的「主机凭据」小节）。
    /// </summary>
    /// <param name="configJson">凭据载荷 JSON。</param>
    /// <returns>Basic 凭据。</returns>
    /// <exception cref="Common.BusinessException">载荷非法或缺少必填字段（错误码 <c>1301</c>）。</exception>
    public static DockerBasicCredentials Parse(string? configJson)
    {
        var payload = CredentialsJson.Deserialize<Payload>(configJson, Description);

        return new DockerBasicCredentials
        {
            Username = CredentialsJson.RequireText(payload.Username, nameof(Payload.Username), Description),
            // 密码只判空、不去空白：口令首尾的空格是合法字符，Trim 会把它悄悄改掉。
            Password = String.IsNullOrEmpty(payload.Password)
                ? throw new Common.BusinessException(
                    Common.ErrorCodes.HostUnreachable,
                    $"{Description} 凭据缺少必填字段 {nameof(Payload.Password)}")
                : payload.Password,
        };
    }

    /// <summary>
    /// 只输出用户名，<b>绝不</b>输出密码。
    /// </summary>
    /// <returns>不含敏感内容的描述。</returns>
    public override string ToString() => $"DockerBasicCredentials(username={this.Username}, password=***)";

    private const string Description = "Basic";

    /// <summary>载荷 DTO：字段可空，由 <see cref="Parse"/> 逐个校验并给出可读错误。</summary>
    private sealed record Payload
    {
        public string? Username { get; init; }

        public string? Password { get; init; }
    }
}
