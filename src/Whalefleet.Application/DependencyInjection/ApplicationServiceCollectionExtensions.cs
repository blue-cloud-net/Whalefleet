using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Abstractions.Terminal;
using Whalefleet.Application.Auth;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.HostGroups;
using Whalefleet.Application.HostTags;
using Whalefleet.Application.Hosts;
using Whalefleet.Application.Images;
using Whalefleet.Application.Networks;
using Whalefleet.Application.Permissions;
using Whalefleet.Application.Repositories;
using Whalefleet.Application.Users;
using Whalefleet.Application.Volumes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whalefleet.Application.DependencyInjection;

/// <summary>
/// Application 层服务注册扩展。
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Application 层服务。
    /// </summary>
    /// <remarks>
    /// <para>外部资源接口（<c>IDockerClientFactory</c> / <c>IPasswordHasher</c> / <c>ITokenService</c> /
    /// <c>ILoginAttemptTracker</c> 等）的接口定义在本层、实现在 Infrastructure 层，
    /// 因此这里<b>只</b>注册用例服务。</para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根（当前未使用，预留给后续应用配置）。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 实例，便于链式调用。</returns>
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 认证用例（0.0.4）。Scoped：依赖 Scoped 的仓储（其取值来自当前请求）。
        services.AddScoped<AuthService>();

        // 主机管理用例（0.0.8）。同样 Scoped：依赖 Scoped 的仓储与主机访问器。
        services.AddScoped<HostService>();

        // 用户管理用例（0.0.8）。
        services.AddScoped<UserService>();

        // 容器查询用例（0.0.9）。Scoped 是因为主机访问器与仓储都是 Scoped，
        // Docker 客户端本身的复用由 Singleton 的客户端池负责，不靠本服务的生命周期。
        services.AddScoped<ContainerService>();

        // 容器健康与退出原因的批量查询（0.6.0 列表健康列的数据源）。Scoped：同上。
        services.AddScoped<ContainerHealthService>();

        // 主机查询用例（可见主机列表 + 单台概览）。Scoped：依赖 Scoped 的仓储、
        // 权限服务与当前用户上下文。
        services.AddScoped<HostQueryService>();

        // 主机磁盘用量（0.6.0）。Scoped：只依赖 Scoped 的主机访问器。
        services.AddScoped<HostDiskUsageService>();

        // 镜像用例（0.0.10）。Scoped：依赖 Scoped 的主机访问器。
        services.AddScoped<ImageService>();

        // 镜像构建与推送（0.4.0）。Scoped：依赖 Scoped 的主机访问器与 仓库凭据解析器。
        services.AddScoped<ImageBuildPushService>();

        // 主机分组用例（0.0.13）。Scoped：依赖 Scoped 的仓储。
        services.AddScoped<HostGroupService>();

        // 主机标签用例（0.6.0）。Scoped：依赖 Scoped 的标签 / 主机仓储。
        services.AddScoped<HostTagService>();

        // 角色管理用例（0.1.0）。Scoped：依赖 Scoped 的角色 / 分配仓储。
        services.AddScoped<RoleService>();

        // 角色分配用例（0.1.0）。Scoped：依赖 Scoped 的分配 / 角色 / 用户 / 主机仓储。
        services.AddScoped<RoleAssignmentService>();

        // 数据卷用例（0.2.0）。Scoped：与镜像同构，只依赖 Scoped 的主机访问器，
        // Docker 客户端本身的复用由 Singleton 的客户端池负责。
        services.AddScoped<VolumeService>();

        // 卷用量的短时缓存（见 VolumeUsageCache 的说明）：必须 **Singleton**——
        // 它的全部价值就是在多个请求之间复用一次几秒钟的 /system/df。
        services.AddSingleton<VolumeUsageCache>();

        // 网络用例（0.2.0）。Scoped：同上。
        services.AddScoped<NetworkService>();

        // 仓库配置用例（0.4.0）。Scoped：依赖 Scoped 的仓储与口令保护器。
        // 同时以 IRepositoryCredentialResolver 的身份暴露**同一实例**——
        // 镜像推送去拿凭据时必须走同一套解密逻辑，不能另起一份实现。
        services.AddScoped<RepositoryService>();
        services.AddScoped<IRepositoryCredentialResolver>(static provider =>
            provider.GetRequiredService<RepositoryService>());

        // 容器采样共享器（0.6.0）：**Singleton**——它就是「每台容器一条 Docker 流」的复用点，
        // 它的状态（引用计数与环形缓冲）必须在所有连接、所有请求之间共享。
        services.AddSingleton<IContainerStatsSampler, ContainerStatsSampler>();

        // 容器事件总线（0.5.0）：**Singleton**——它就是一条进程内的广播通道，
        // 它的「状态」是订阅方列表。注册成 Scoped 会让每个请求拿到一个互不相识的实例，
        // 事件根本传不出去。
        services.AddSingleton<IContainerEventBus, InProcessContainerEventBus>();

        // 实时通道选项（0.5.0）的**形状**定义在应用层（应用服务与 Hub 都要读它：
        // 订阅上限是 Hub 的约束，而频道容量 / 采样间隔是流服务的约束），
        // 但**绑定与校验放在 Api 层**——配置绑定是入站适配器的职责，
        // 而应用层不引入配置绑定相关的包。
        // 详见 Api/DependencyInjection/RealtimeServiceCollectionExtensions.cs。
        //
        // 这里仍然注册一次「空」的选项类型：保证只用应用层时
        // IOptions<RealtimeOptions> 也能解析（拿到默认值），而不是在 DI 里报「找不到服务」。
        services.AddOptions<RealtimeOptions>();

        // 容器日志流与资源采样流（0.5.0）。Scoped：依赖 Scoped 的主机访问器。
        // 流的生命周期由订阅者的连接决定，与 HTTP 请求无关。
        services.AddScoped<ContainerStreamService>();
        services.AddScoped<IContainerLogProvider>(static provider =>
            provider.GetRequiredService<ContainerStreamService>());
        services.AddScoped<IContainerStatsProvider>(static provider =>
            provider.GetRequiredService<ContainerStreamService>());

        // 容器交互式终端（0.8.0）。Scoped：依赖 Scoped 的主机访问器与权限判定
        // （终端是「某个人在某台主机上的某条会话」，天然带请求身份）。
        services.AddScoped<ContainerTerminalService>();

        // 终端选项（0.8.0）的**形状**定义在应用层（用例服务要用它做白名单与上限判定），
        // 而**绑定与校验放在 Api 层**——与 RealtimeOptions 同一处理方式。
        // 这里注册一次「空」的选项类型，保证只用应用层时也能解析出默认值。
        services.AddOptions<TerminalOptions>();

        // 终端会话计数器（0.8.0）：**Singleton**——它数的是「所有连接」，
        // 注册成 Scoped 等于没有上限。
        services.AddSingleton<TerminalSessionRegistry>();

        return services;
    }
}