using Microsoft.Extensions.Configuration;

namespace Docker.Manager.Api.Configuration;

/// <summary>
/// 各配置节的读取入口。
/// </summary>
/// <remarks>
/// 刻意<b>不在</b> DI 注册期读配置：那些时刻配置源尚未完全构建，
/// 读到的会是空值（本仓库在 CORS 策略上已真实踩过这个坑——
/// 注册期读不到值，于是静默不注册任何策略）。
/// 因此这里由 <c>Program.cs</c> 在建好应用之后显式调用。
/// </remarks>
internal static class ConfigurationReader
{
    /// <summary>
    /// 读取 <c>Spa</c> 配置节。
    /// </summary>
    /// <param name="configuration">配置根。</param>
    /// <returns>SPA 配置；未配置时返回带安全默认值的实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> 为 <see langword="null"/>。</exception>
    public static SpaOptions ReadSpa(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SpaOptions();
        configuration.GetSection(SpaOptions.SectionName).Bind(options);
        return options;
    }
}