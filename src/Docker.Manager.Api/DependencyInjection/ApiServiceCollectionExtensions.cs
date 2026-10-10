using Docker.Manager.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AspNetCorsOptions = Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions;

namespace Docker.Manager.Api.DependencyInjection;

/// <summary>
/// Api 层服务注册扩展。
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// 注册 CORS 白名单策略。
    /// </summary>
    /// <remarks>
    /// <para><b>默认拒绝</b>：当 <c>Cors:AllowedOrigins</c> 为空（默认）时，<b>不注册默认策略</b>，
    /// 因此不会发出任何 <c>Access-Control-*</c> 响应头，浏览器端等价于完全拒绝跨域。</para>
    /// <para>白名单非空时注册默认策略，并开启 <c>AllowCredentials</c>：本 API 依赖
    /// <c>Authorization</c> 头携带 JWT，本身不需要凭据模式，但开启后可在 0.3.0 引入 SignalR
    /// （JS 客户端默认 <c>withCredentials: true</c>）时无需再改配置。
    /// 由于白名单是<b>显式枚举</b>的具体 origin（而非 <c>AllowAnyOrigin</c>），
    /// 该放宽被严格限制在运营者信任的来源内。</para>
    /// <para><c>AllowAnyHeader</c> 是必要的：前端需发送 <c>Authorization</c>
    /// 与 <c>X-Host-Id</c> 等自定义头。</para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    public static IServiceCollection AddApiCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(CorsOptions.SectionName);

        services
            .AddOptions<CorsOptions>()
            .Bind(section)
            .Validate(
                options => options.AllowedOrigins.All(CorsOptions.IsValidOrigin),
                "Cors:AllowedOrigins 的每一项都必须是绝对 http/https URI，且不能以 '/' 结尾")
            .ValidateOnStart();

        // 策略注册必须延迟到选项解析期：DI 注册期读配置会拿到尚未定型的值（可能为空），
        // 导致静默地不注册任何策略。详见 CorsWhitelistPolicyConfigurator 的 remarks。
        services.AddCors();
        services.AddSingleton<IConfigureOptions<AspNetCorsOptions>, CorsWhitelistPolicyConfigurator>();

        return services;
    }
}
