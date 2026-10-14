using Microsoft.Extensions.Options;
using AspNetCorsOptions = Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions;

namespace Whalefleet.Api.Configuration;

/// <summary>
/// 依据 <see cref="CorsOptions"/> 白名单，延迟注册 ASP.NET Core 的 CORS 默认策略。
/// </summary>
/// <remarks>
/// <para><b>为什么要延迟（<see cref="IConfigureOptions{TOptions}"/>）而不是在
/// <c>AddApiCors</c> 里直接读配置</b>：DI 注册发生在 <c>WebApplication.CreateBuilder</c> 之后立刻执行，
/// 此时配置源可能尚未全部就位——例如集成测试通过 <c>WebApplicationFactory.WithSetting</c>
/// 注入的内存配置、或未来接入的延迟配置提供程序。
/// 在注册期读配置会得到空值，从而静默地不注册任何默认策略（跨域全部被拒绝，且难以排查）。
/// 改为在选项解析期（宿主构建完成后）读取，可保证拿到最终配置。</para>
/// <para><b>默认拒绝</b>：白名单为空时不添加任何策略。ASP.NET Core 的
/// <c>DefaultCorsPolicyProvider</c> 取不到策略即返回 <c>null</c>，不会发出任何
/// <c>Access-Control-*</c> 响应头。</para>
/// </remarks>
internal sealed class CorsWhitelistPolicyConfigurator : IConfigureOptions<AspNetCorsOptions>
{
    private readonly IOptions<CorsOptions> _whitelist;

    /// <summary>
    /// 初始化 <see cref="CorsWhitelistPolicyConfigurator"/>。
    /// </summary>
    /// <param name="whitelist">CORS 白名单选项。</param>
    public CorsWhitelistPolicyConfigurator(IOptions<CorsOptions> whitelist)
    {
        ArgumentNullException.ThrowIfNull(whitelist);

        _whitelist = whitelist;
    }

    /// <inheritdoc />
    public void Configure(AspNetCorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var allowedOrigins = _whitelist.Value.AllowedOrigins;

        if (allowedOrigins.Length == 0)
        {
            return;
        }

        options.AddDefaultPolicy(policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    }
}
