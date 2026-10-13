namespace Docker.Manager.Infrastructure.Security;

/// <summary>
/// JWT 与刷新令牌的配置（配置节 <c>Jwt</c>）。
/// </summary>
/// <remarks>
/// <para><b>签名密钥</b>：<see cref="Secret"/> 在生产环境<b>必须</b>来自环境变量 <c>JWT__SECRET</c>
/// （32 字节以上）；缺失或过短会导致<b>启动失败</b>（<c>ValidateOnStart</c>），
/// 而不是运行到第一次登录才暴露。</para>
/// <para><b>刷新令牌不是 JWT</b>：它是一串随机字节，因此没有"有效期"之外的配置项；
/// 其长度由 <see cref="RefreshTokenSizeBytes"/> 决定（默认 32 字节 = 256 位熵）。</para>
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// 签名密钥的最小长度（字节）。HS256 的密钥短于摘要长度会显著削弱安全性。
    /// </summary>
    public const int MinSecretLengthBytes = 32;

    /// <summary>
    /// HS256 签名密钥。
    /// </summary>
    /// <remarks>
    /// 可空是刻意的：配置可能缺失，而"缺失"必须表现为启动失败（由校验器负责），
    /// 不能在类型层面假设它一定存在（那会得到启动时的 <see cref="NullReferenceException"/>）。
    /// </remarks>
    public string? Secret { get; set; }

    /// <summary>
    /// 签发者（<c>iss</c> 声明）。默认 <c>docker-manager</c>。
    /// </summary>
    public string Issuer { get; set; } = "docker-manager";

    /// <summary>
    /// 受众（<c>aud</c> 声明）。默认 <c>docker-manager</c>。
    /// </summary>
    public string Audience { get; set; } = "docker-manager";

    /// <summary>
    /// access token 有效期。默认 15 分钟。
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// refresh token 有效期。默认 7 天。
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// refresh token 原始长度（字节）。默认 32。
    /// </summary>
    /// <remarks>
    /// 一旦降低该值，已签发的旧令牌仍按原长度校验——因为它们只按<b>哈希</b>查库，
    /// 长度只影响新令牌的熵，不影响旧令牌的可验证性。
    /// </remarks>
    public int RefreshTokenSizeBytes { get; set; } = 32;
}
