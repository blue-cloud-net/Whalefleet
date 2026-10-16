using System.Text;
using Whalefleet.Api.Security;
using Whalefleet.Application.Abstractions;
using Whalefleet.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Whalefleet.Api.DependencyInjection;

/// <summary>
/// Api 层的鉴权注册扩展。
/// </summary>
/// <remarks>
/// <para>本扩展做三件事：配置 JWT 校验参数、注册鉴权中间件、把 <c>ICurrentUser</c>
/// 换成基于 <see cref="HttpContext"/> 的真实实现。</para>
/// <para><b>必须在 <c>AddInfrastructure</c> 之后调用</b>：<c>ICurrentUser</c> 的占位注册由
/// <c>AddPersistence</c>（经 <c>AddInfrastructure</c>）写入，这里用 <c>Replace</c> 覆盖它。</para>
/// </remarks>
public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 JWT 鉴权、鉴权中间件与当前用户解析。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（读取 <c>Jwt</c> 节）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 校验参数用 Options 配置而不是在此直接读配置：
        // 访问 IOptions<JwtOptions>.Value 会触发 Infrastructure 注册的启动校验器，
        // 于是"密钥缺失/过短"在这里抛出可读的 OptionsValidationException，
        // 而不是在后面拿 null 去 GetBytes 得到一个 NullReferenceException。
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptionsAccessor) =>
            {
                var jwt = jwtOptionsAccessor.Value;

                // 关闭入站声明映射：保持令牌里的原始短名（sub / unique_name / role），
                // 使 JwtClaimNames 与令牌内容一一对应，不依赖框架的默认映射表。
                bearer.MapInboundClaims = false;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwt.Secret!)),
                    ValidateLifetime = true,

                    // 30 秒容差：服务端与客户端的时钟很难完全一致，
                    // 零容差会把"刚登录就拿到的令牌"判为未生效/已过期。
                    ClockSkew = TimeSpan.FromSeconds(30),

                    // 显式指定声明名，配合 MapInboundClaims = false 使角色/用户名读取有唯一解。
                    RoleClaimType = JwtClaimNames.Role,
                    NameClaimType = JwtClaimNames.UniqueName,
                };

                bearer.Events = new JwtBearerEvents
                {
                    // 让 401/403 也返回统一的 ApiResult，并区分「未认证」与「已过期」。
                    OnChallenge = JwtBearerResponseEvents.OnChallengeAsync,
                    OnForbidden = JwtBearerResponseEvents.OnForbiddenAsync,
                };
            });

        // AddJwtBearer 的配置走上面的 Options，因此这里不需要内联 lambda。
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // 当前用户解析：Api 层实现替换 Infrastructure 的占位实现（SystemCurrentUser）。
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Scoped<ICurrentUser, HttpContextCurrentUser>());

        services.AddPermissionAuthorization();

        return services;
    }
}
