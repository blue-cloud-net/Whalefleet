using Whalefleet.Application.Abstractions.Terminal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whalefleet.Api.Terminal;

/// <summary>
/// 交互式终端（0.8.0）的注册扩展。
/// </summary>
/// <remarks>
/// <para><b>为什么选项绑定放在 Api 层</b>：与 <c>RealtimeOptions</c> 同一处理——
/// 选项的<b>形状</b>定义在应用层（用例服务要读它），而<b>绑定与校验</b>属于入站适配器的职责，
/// 应用层不引入配置绑定相关的包。</para>
/// <para><b>为什么全部 <c>ValidateOnStart</c></b>：白名单写错（相对路径 / 空串）或会话上限写成 0
/// 都是「启动时就该知道配错了」的事。放到运行时，症状分别是「某个 shell 永远打不开」
/// 与「终端一个都开不了」，而两者都看不出是配置问题。</para>
/// </remarks>
public static class TerminalServiceCollectionExtensions
{
    /// <summary>
    /// 注册终端的选项与会话计数器。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（读取 <c>Terminal</c> 节）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IServiceCollection AddApiTerminal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<TerminalOptions>()
            .Bind(configuration.GetSection(TerminalOptions.SectionName))
            .Validate(
                static options => IsValid(options),
                "Terminal 配置非法：DefaultShell 必须在 AllowedShells 内，"
                + "AllowedShells 必须是绝对路径且非空，TokenLifetime 须在 (0, 1 小时]，"
                + "MaxSessionsPerUser 须 ≥ 1，KeepAliveSeconds 须 ≥ 5")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// 校验终端选项。
    /// </summary>
    /// <param name="options">待校验的选项。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    private static bool IsValid(TerminalOptions options)
    {
        if (options.AllowedShells.Length == 0)
        {
            return false;
        }

        if (options.AllowedShells.Any(shell =>
                String.IsNullOrWhiteSpace(shell) || !shell.StartsWith('/')))
        {
            return false;
        }

        if (!options.AllowedShells.Contains(options.DefaultShell, StringComparer.Ordinal))
        {
            return false;
        }

        return options.TokenLifetime > TimeSpan.Zero
            && options.TokenLifetime <= TimeSpan.FromHours(1)
            && options.MaxSessionsPerUser >= 1
            && options.KeepAliveSeconds >= 5;
    }
}
