using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Files;

namespace Whalefleet.Api.DependencyInjection;

/// <summary>
/// Api 层的文件传输注册扩展（0.14.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么选项的绑定放在这里</b>：配置绑定是<b>入站适配器</b>的职责，
/// 而应用层不引入配置绑定相关的包。选项的<b>形状</b>定义在应用层
/// （<see cref="FilesOptions"/>），因为用例服务与后台工作器都要读它。</para>
/// <para><b>为什么必须 <c>ValidateOnStart</c></b>：非法的值（例如把上传上限配成 0）
/// 必须在启动时炸，而不是在第一条上传请求时表现为「上传总是失败」——
/// 那种故障在页面上几乎无从定位。</para>
/// </remarks>
public static class FilesServiceCollectionExtensions
{
    /// <summary>
    /// 注册文件传输相关服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（读取 <c>Files</c> 节）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IServiceCollection AddApiFiles(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FilesOptions>()
            .Bind(configuration.GetSection(FilesOptions.SectionName))
            .Validate(
                static options => options.MaxUploadBytes > 0,
                "Files:MaxUploadBytes 必须为正数")
            .Validate(
                static options => options.MaxConcurrentExports > 0,
                "Files:MaxConcurrentExports 必须为正数")
            .Validate(
                static options => options.TicketTtl > TimeSpan.Zero,
                "Files:TicketTtl 必须为正值")
            .Validate(
                static options => options.ExportTtl > TimeSpan.Zero,
                "Files:ExportTtl 必须为正值")
            .Validate(
                static options => !String.IsNullOrWhiteSpace(options.HelperImage),
                "Files:HelperImage 不能为空")
            .ValidateOnStart();

        // 导出后台工作器：HostedService 天然是单例，且它自己按任务建作用域。
        services.AddHostedService<TransferJobWorker>();

        // 过期清理：启动时清遗留产物，运行时周期扫描。
        services.AddHostedService<TransferCleanupService>();

        return services;
    }
}
