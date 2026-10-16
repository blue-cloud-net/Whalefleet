using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Whalefleet.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Whalefleet.Infrastructure.Security;

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
    public AccessToken CreateAccessToken(
        Guid userId,
        string username,
        IReadOnlyList<string> roleNames,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(roleNames);

        var expiresAt = utcNow.Add(_options.AccessTokenLifetime);

        // 多角色签成多个同名的 claim（而不是一个数组 claim）：
        // JwtSecurityTokenHandler 会把同名 claim 依次写进数组元素，
        // 读侧用 FindAll 拿回全部。而数组 claim 需要额外的 claim-mapping 配置，
        // 且部分第三方验证库对它的处理不一致——多值 claim 在 JWT 里就是数组，不值得为它绕路。
        //
        // ⚠️ 必须走 Subject（ClaimsIdentity）而不是 descriptor.Claims：
        // 后者的类型是 IDictionary<string, object>，**同一个键只能有一个值**，
        // 于是 `claims[Role] = name` 每轮都在覆盖前一角色，最终令牌里只剩最后一个。
        // 症状极隐蔽：单角色用户一切正常，只有真正持有多角色的人才发现授权范围不对。
        var identity = new ClaimsIdentity(string.Empty, JwtClaimNames.UniqueName, JwtClaimNames.Role);
        identity.AddClaim(new Claim(JwtClaimNames.Subject, userId.ToString("D")));
        identity.AddClaim(new Claim(JwtClaimNames.UniqueName, username));
        identity.AddClaim(new Claim(JwtClaimNames.Jti, Guid.NewGuid().ToString("N")));

        foreach (var roleName in roleNames.Where(name => !String.IsNullOrWhiteSpace(name)))
        {
            identity.AddClaim(new Claim(JwtClaimNames.Role, roleName));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = utcNow,
            Expires = expiresAt,
            SigningCredentials = _signingCredentials,

            // 刻意不设 NotBefore：客户端与服务端的时钟若相差几秒，
            // nbf 会让"刚登录成功就拿到的令牌"立刻被判定为无效。
            Subject = identity,
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
