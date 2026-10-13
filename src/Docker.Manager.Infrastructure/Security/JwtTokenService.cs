using System.Security.Cryptography;
using System.Text;
using Docker.Manager.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Docker.Manager.Infrastructure.Security;

/// <summary>
/// 基于 <c>Microsoft.IdentityModel.JsonWebTokens</c> 的令牌服务。
/// </summary>
/// <remarks>
/// <para><b>签名算法固定 HS256</b>：单实例自托管部署下对称密钥最省事，没有公钥分发问题。
/// 算法写死在实现里而非配置项——"可配置的签名算法"是一类著名的漏洞来源
/// （攻击者可以把 <c>alg</c> 改成 <c>none</c> 或非对称算法来欺骗验证方）。</para>
/// <para><b>刷新令牌只存哈希</b>：数据库里是 <c>base64url(SHA-256(原文))</c>。
/// 该格式是<b>持久化契约</b>，一旦变更就要求所有用户重新登录，因此实现刻意不依赖任何库的
/// Base64 辅助方法，保证编码跨库、跨版本稳定。</para>
/// <para><b>无状态且线程安全</b>：<see cref="JsonWebTokenHandler"/> 可复用，注册为 Singleton。</para>
/// </remarks>
internal sealed class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _signingCredentials;

    /// <summary>
    /// 初始化令牌服务。
    /// </summary>
    /// <param name="options">JWT 配置（启动时已校验 <c>Secret</c> 非空且足够长）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    public JwtTokenService(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;

        // 校验器已保证 Secret 非空且足够长；此处用 ! 是安全的，异常消息也说明了原因。
        var keyBytes = Encoding.UTF8.GetBytes(_options.Secret!);
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    public AccessToken CreateAccessToken(Guid userId, string username, string roleName, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        var expiresAt = utcNow.Add(_options.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = utcNow,
            Expires = expiresAt,
            SigningCredentials = _signingCredentials,

            // 刻意不设 NotBefore：客户端与服务端的时钟若相差几秒，
            // nbf 会让"刚登录成功就拿到的令牌"立刻被判定为无效。
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtClaimNames.Subject] = userId.ToString("D"),
                [JwtClaimNames.UniqueName] = username,
                [JwtClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [JwtClaimNames.Role] = roleName,
            },
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }

    /// <inheritdoc />
    public RefreshTokenValue CreateRefreshToken(DateTime utcNow)
    {
        var rawToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(_options.RefreshTokenSizeBytes));

        return new RefreshTokenValue(
            rawToken,
            HashRefreshToken(rawToken),
            utcNow.Add(_options.RefreshTokenLifetime));
    }

    /// <inheritdoc />
    public string HashRefreshToken(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

        return Base64UrlEncode(digest);
    }

    /// <summary>
    /// Base64url 编码（无填充）。对空字节串返回空串。
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
