using System.Security.Claims;
using System.Text;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Terminal;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 基于 HS256 的 terminal token 服务（<b>与 access token 共用密钥，但 audience 不同</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么复用签名密钥而不是另建一把</b>：另一把密钥意味着另一份必须随数据库一起备份的
/// 秘密文件（<c>jwt.key</c> 已经踩过这个坑：漏备份 = 恢复后所有人被静默登出）。这里的安全性
/// 不来自「不同的密钥」，而来自 <b>audience 隔离</b>——主鉴权只接受主 audience，
/// 因此 terminal token 天然进不了普通 API。</para>
/// <para><b>为什么不加「令牌类型」校验来拒绝</b>：额外加一段 <c>OnTokenValidated</c> 里比对
/// <c>typ</c> 的代码，就等于给「漏判即提权」留了一个位置。audience 是框架<b>默认就在校验</b>的东西，
/// 少写一段代码就少一个出错点。声明里仍保留 <c>typ=terminal</c>，但只用于自检与排障。</para>
/// <para><b>校验失败一律返回 <see langword="null"/></b>：调用方对每种失败的处理完全相同，
/// 区分「过期 / 签名错 / 格式错」只会诱使把差异透给客户端。</para>
/// </remarks>
internal sealed class JwtTerminalTokenService : ITerminalTokenService
{
    /// <summary>
    /// terminal token 的受众。
    /// </summary>
    /// <remarks>
    /// <b>必须与 <see cref="JwtOptions.Audience"/> 不同</b>——这正是「terminal token 不能当
    /// access token 用」的全部机制。改这个值时请一并确认主鉴权的 <c>ValidAudience</c> 没有跟着改。
    /// </remarks>
    public const string TerminalAudience = "whalefleet-terminal";

    /// <summary>
    /// 令牌类型声明名（自定义短名）。
    /// </summary>
    public const string TokenTypeClaim = "typ";

    /// <summary>
    /// 令牌类型取值。
    /// </summary>
    public const string TokenTypeValue = "terminal";

    private const string HostClaim = "host";
    private const string ContainerClaim = "container";
    private const string ShellClaim = "shell";
    private const string CommandClaim = "cmd";

    /// <summary>
    /// 允许的时钟偏差（与主鉴权的 30 秒一致）。
    /// </summary>
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(30);

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validation;
    private readonly JwtOptions _jwtOptions;
    private readonly TerminalOptions _terminalOptions;

    /// <summary>
    /// 初始化 terminal token 服务。
    /// </summary>
    /// <param name="jwtOptions">JWT 配置（提供签名密钥与签发者）。</param>
    /// <param name="terminalOptions">终端配置（提供有效期）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public JwtTerminalTokenService(
        IOptions<JwtOptions> jwtOptions,
        IOptions<TerminalOptions> terminalOptions)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);
        ArgumentNullException.ThrowIfNull(terminalOptions);

        _jwtOptions = jwtOptions.Value;
        _terminalOptions = terminalOptions.Value;

        // 校验器已保证 Secret 非空且足够长；此处用 ! 是安全的。
        var keyBytes = Encoding.UTF8.GetBytes(_jwtOptions.Secret!);

        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);

        _validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = TerminalAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateLifetime = true,

            // 与主鉴权同一容差：服务端与客户端的时钟差几秒不该让刚签发的令牌失效。
            ClockSkew = AllowedClockSkew,

            // 显式限定算法：不设它时验证方会接受令牌头里声明的任何对称算法。
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    }

    /// <inheritdoc />
    public TerminalToken Create(TerminalSessionGrant grant, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.HostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.ContainerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.Shell);

        var expiresAt = utcNow.Add(_terminalOptions.TokenLifetime);

        // 所有声明都写进 ClaimsIdentity（而不是 descriptor.Claims 字典）：
        // 后者同一个键只能有一个值，而这里的语义就是"一个 claim 一个值"，
        // 保持与 JwtTokenService 一致的写法，避免两处风格分叉。
        var identity = new ClaimsIdentity(string.Empty, JwtClaimNames.UniqueName, JwtClaimNames.Role);
        identity.AddClaim(new Claim(JwtClaimNames.Subject, grant.UserId.ToString("D")));
        identity.AddClaim(new Claim(JwtClaimNames.Jti, Guid.NewGuid().ToString("N")));
        identity.AddClaim(new Claim(TokenTypeClaim, TokenTypeValue));
        identity.AddClaim(new Claim(HostClaim, grant.HostId));
        identity.AddClaim(new Claim(ContainerClaim, grant.ContainerId));
        identity.AddClaim(new Claim(ShellClaim, grant.Shell));

        if (!String.IsNullOrEmpty(grant.Command))
        {
            identity.AddClaim(new Claim(CommandClaim, grant.Command));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwtOptions.Issuer,
            Audience = TerminalAudience,
            IssuedAt = utcNow,
            Expires = expiresAt,
            SigningCredentials = _signingCredentials,
            Subject = identity,
        };

        return new TerminalToken(_handler.CreateToken(descriptor), expiresAt);
    }

    /// <inheritdoc />
    public async Task<TerminalSessionGrant?> ValidateAsync(
        string token,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await _handler
            .ValidateTokenAsync(token, _validation)
            .ConfigureAwait(false);

        if (!result.IsValid)
        {
            return null;
        }

        // 生命周期校验由 TokenValidationParameters 完成（用系统时钟）；
        // 这里再用调用方传入的时间判一次，使"过期"可以用假时间确定性地测出来，
        // 而不必真的等 5 分钟。两处容差保持一致，否则会出现"框架说过期、这里说没过期"。
        var issued = result.SecurityToken as JsonWebToken;
        var expiresAt = issued?.ValidTo ?? DateTime.MinValue;

        if (expiresAt != DateTime.MinValue && utcNow > expiresAt.Add(AllowedClockSkew))
        {
            return null;
        }

        var claims = result.Claims;

        // 自检：类型不符或身份 / 目标字段缺失都当作无效。
        // 这些字段由本服务自己写入，缺失只可能意味着"被改造过"或"密钥被换过"。
        if (!claims.TryGetValue(TokenTypeClaim, out var type)
            || !String.Equals(type as string, TokenTypeValue, StringComparison.Ordinal))
        {
            return null;
        }

        if (!claims.TryGetValue(JwtClaimNames.Subject, out var subject)
            || subject is not string subjectValue
            || !Guid.TryParse(subjectValue, out var userId))
        {
            return null;
        }

        if (!claims.TryGetValue(HostClaim, out var host)
            || host is not string hostId
            || String.IsNullOrWhiteSpace(hostId))
        {
            return null;
        }

        if (!claims.TryGetValue(ContainerClaim, out var container)
            || container is not string containerId
            || String.IsNullOrWhiteSpace(containerId))
        {
            return null;
        }

        if (!claims.TryGetValue(ShellClaim, out var shell) || shell is not string shellPath)
        {
            return null;
        }

        var command = claims.TryGetValue(CommandClaim, out var raw) ? raw as string : null;

        cancellationToken.ThrowIfCancellationRequested();

        return new TerminalSessionGrant(userId, hostId, containerId, shellPath, command);
    }
}
