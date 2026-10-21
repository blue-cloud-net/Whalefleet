using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whalefleet.Api.Realtime;
using Whalefleet.Application.Abstractions.Realtime;

namespace Whalefleet.Api.DependencyInjection;

/// <summary>
/// Api 层的实时通道注册扩展。
/// </summary>
/// <remarks>
/// <para><b>为什么选项的绑定放在这里</b>：配置绑定是<b>入站适配器</b>的职责，
/// 而应用层不引入配置绑定相关的包。选项的<b>形状</b>定义在应用层
/// （<see cref="RealtimeOptions"/>），因为应用服务与 Hub 都要读它。</para>
/// <para><b>为什么必须 <c>ValidateOnStart</c></b>：非法值（例如把订阅上限配成 0）
/// 必须在启动时炸，而不是在第一条日志到达时表现为「什么都不推」——
/// 那种故障在页面上表现为「实时日志没反应」，几乎无从定位。</para>
/// </remarks>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// 注册实时通道（SignalR）相关服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（读取 <c>Realtime</c> 节）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IServiceCollection AddApiRealtime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .Validate(
                static options => options.IsValid(),
                "Realtime 配置非法：订阅上限 / 批量行数 / 冲刷间隔 / 频道容量 / 采样间隔 / 枚举间隔 / 重连退避 必须为正值，"
                + "且频道容量不得小于批量行数、采样间隔不得小于 1000 毫秒")
            .ValidateOnStart();

        // SignalR 本身。默认传输（WebSocket 优先，保留 SSE / LongPolling 回退）——
        // 反代不支持 upgrade 时仍能连上，只是多几次往返。
        services.AddSignalR();

        // 订阅表与连接表：生命周期跨多个 Hub 调用，必须是单例。
        services.AddSingleton<RealtimeSubscriptionRegistry>();
        services.AddSingleton<RealtimeConnectionRegistry>();

        // 主机解析与权限判定：Scoped（依赖 Scoped 的主机访问器与权限服务）。
        services.AddScoped<RealtimeHostResolver>();

        // 进度发布器：接口在应用层，实现在这里——「怎么推」是入站适配器的事。
        services.AddSingleton<IOperationProgressPublisher, HubOperationProgressPublisher>();

        // 通知派发（0.5.0）：订阅事件总线，把容器事件推给有权的在线连接。
        services.AddHostedService<ContainerNotificationDispatcher>();

        return services;
    }
}
