using System.Globalization;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Hosts;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机查询用例（可见主机列表 + 单台概览），对应 <c>/api/v1/hosts/*</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个服务而不是并进 <c>HostService</c></b>：那是管理员的主机 CRUD
/// （增删改端点、凭据、客户端失效），而这里是任意已认证用户都能看的只读查询。
/// 混在一起会让「哪些方法需要管理员」只能靠读方法体去猜。</para>
/// <para><b>可见性判定复用权限服务</b>：列表要按「这个人能看到哪些主机」**过滤**
/// （<see cref="IPermissionChecker.FilterVisibleHostsAsync"/>），
/// 单台概览则由 <c>CurrentHostMiddleware</c> 在进入本服务前**拒绝**。两者用的是同一条规则，
/// 因此都走同一个权限服务，避免两处各写一套而漂移。</para>
/// <para><b>离线主机不报错</b>：主机离线要在界面上显示成「离线」而不是一个错误页，
/// 因此取不到数据时返回 <c>status=offline</c> 与空字段，只有「主机本身不存在」才抛 <c>1300</c>。</para>
/// </remarks>
public sealed class HostQueryService
{
    private readonly IHostRepository _hosts;
    private readonly IDockerHostAccessor _accessor;
    private readonly IPermissionChecker _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<HostQueryService> _logger;

    /// <summary>
    /// 初始化主机查询用例。
    /// </summary>
    /// <param name="hosts">主机仓储。</param>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="permissions">权限判定（列表过滤用）。</param>
    /// <param name="currentUser">当前登录用户（权限判定的主体）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HostQueryService(
        IHostRepository hosts,
        IDockerHostAccessor accessor,
        IPermissionChecker permissions,
        ICurrentUser currentUser,
        ILogger<HostQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(logger);

        _hosts = hosts;
        _accessor = accessor;
        _permissions = permissions;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// 列出当前用户可见的主机（含连通状态，**不含**资源计数与版本）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可见主机列表，顺序与仓储约定一致（创建时间升序，同时间按名称）。</returns>
    /// <remarks>
    /// <para><b>列表里不带 <see cref="HostOverviewSummary.Counts"/> / <see cref="HostOverviewSummary.Version"/></b>：
    /// 取它们要额外调用守护进程（版本一次、计数四次），主机一多列表就会明显变慢甚至超时。
    /// 这两项由概览接口按需取——列表只做每台一次的轻量探测。</para>
    /// <para><b>探测是并行的</b>：串行时 N 台离线主机各自等一次超时，列表页会慢到不可用。</para>
    /// <para><b>非管理员只看到被授权的主机</b>：过滤由 <see cref="IPermissionChecker"/>
    /// 一次查库完成（0.1.0 起数据来自带作用域的角色分配）。没被授权时返回<b>空列表</b>
    /// 而不是 403——列表接口一报错，前端的「主机选择器」整块就红了，而它本该显示「暂无可用主机」。</para>
    /// </remarks>
    public async Task<IReadOnlyList<HostOverviewSummary>> ListVisibleAsync(
        CancellationToken cancellationToken = default)
    {
        var hosts = await _hosts.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
        var userId = _currentUser.UserId ?? Guid.Empty;

        var visible = (await _permissions
            .FilterVisibleHostsAsync(userId, hosts, cancellationToken)
            .ConfigureAwait(false)).ToArray();

        var statuses = await Task
            .WhenAll(visible.Select(host => this.ProbeAsync(host, cancellationToken)))
            .ConfigureAwait(false);

        _logger.LogDebug(
            "列出可见主机：{Visible}/{Total} 台，在线 {Online} 台",
            visible.Length,
            hosts.Count,
            statuses.Count(status => status == HostConnectivityStatus.Online));

        return
        [
            .. visible.Select((host, index) => Map(
                host,
                statuses[index],
                counts: null,
                startedAt: null,
                version: null)),
        ];
    }

    /// <summary>
    /// 查询单台主机的概览（版本 + 资源计数 + 启动时间）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机概览；主机离线时除基本字段外均为空。</returns>
    /// <exception cref="BusinessException">主机不存在（<c>1300</c>）。</exception>
    /// <remarks>
    /// 六次调用（版本 / 运行信息 / 容器列表 / 卷列表 / 网络列表 / 镜像磁盘占用）<b>串行</b>：
    /// 它们是同一个守护进程上的本地调用，并行只会让「各自可能失败」变成「丢掉另一个的错误信息」。
    /// 真到了需要压这一步延迟的规模，该做的是缓存，不是并行。
    /// <para>容器列表只为读健康检查计数（<c>/info</c> 不提供）；镜像磁盘占用走
    /// <c>/system/df?type=image</c>，失败时降级为 <see langword="null"/> 而不是让整台主机变离线。</para>
    /// </remarks>
    public async Task<HostOverviewSummary> GetOverviewAsync(
        string hostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        try
        {
            var client = await _accessor.GetClientAsync(host.Name, cancellationToken).ConfigureAwait(false);

            var version = await DockerErrorTranslator.ExecuteAsync(
                () => client.System.GetVersionAsync(cancellationToken),
                $"主机 {host.Name} 的版本信息",
                cancellationToken).ConfigureAwait(false);

            var info = await DockerErrorTranslator.ExecuteAsync(
                () => client.System.GetSystemInfoAsync(cancellationToken),
                $"主机 {host.Name} 的运行信息",
                cancellationToken).ConfigureAwait(false);

            // 健康检查状态既不在 /info 也不在 /containers/{id}/json 之外的任何批量接口里，
            // 想要「一次拿到全部容器的健康状态」只能列一遍容器（Status 文本里带 (healthy)）。
            var containers = await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true },
                    cancellationToken),
                $"主机 {host.Name} 的容器列表",
                cancellationToken).ConfigureAwait(false);

            var volumes = await DockerErrorTranslator.ExecuteAsync(
                () => client.Volumes.ListAsync(cancellationToken),
                $"主机 {host.Name} 的数据卷列表",
                cancellationToken).ConfigureAwait(false);

            var networks = await DockerErrorTranslator.ExecuteAsync(
                () => client.Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken),
                $"主机 {host.Name} 的网络列表",
                cancellationToken).ConfigureAwait(false);

            var (healthy, unhealthy) = CountHealth(containers);
            var imagesSize = await TryGetImagesSizeAsync(client, host.Name, cancellationToken)
                .ConfigureAwait(false);

            var counts = new HostResourceCounts(
                Containers: info.Containers,
                Images: info.Images,
                Volumes: volumes.Volumes?.Count ?? 0,
                Networks: networks.Count,
                ContainersRunning: info.ContainersRunning,
                ContainersStopped: info.ContainersStopped,
                ContainersHealthy: healthy,
                ContainersUnhealthy: unhealthy,
                ImagesSizeBytes: imagesSize);

            _logger.LogDebug(
                "主机 {HostName} 概览：引擎 {Version}，容器 {Containers}（运行 {Running}），镜像 {Images}",
                host.Name,
                version.Version,
                counts.Containers,
                counts.ContainersRunning,
                counts.Images);

            return Map(
                host,
                HostConnectivityStatus.Online,
                counts,
                ParseTimestamp(info.SystemTime),
                MapVersion(version));
        }
        catch (BusinessException exception) when (
            exception.Code is ErrorCodes.HostUnreachable or ErrorCodes.HostDisabled)
        {
            // 离线 / 已停用是**正常状态**，要在界面上显示成「离线」而不是错误。
            // 记 Information 而不是 Warning：主机离线本身不该触发告警（0.4.0 的健康轮询会统一处理）。
            _logger.LogInformation("主机 {HostName} 概览不可用：{Message}", host.Name, exception.Message);

            return Map(
                host,
                HostConnectivityStatus.Offline,
                counts: null,
                startedAt: null,
                version: null);
        }
    }

    /// <summary>健康检查通过的标记（容器列表的 <c>Status</c> 形如 <c>Up 2 hours (healthy)</c>）。</summary>
    private const string HealthHealthyMarker = "(healthy)";

    /// <summary>健康检查失败的标记。</summary>
    private const string HealthUnhealthyMarker = "(unhealthy)";

    /// <summary><c>GET /system/df</c> 的 <c>type</c> 过滤值：只要镜像，跳过容器 / 卷 / 构建缓存。</summary>
    private const string ImageUsageType = "image";

    /// <summary>
    /// 从容器列表的 <c>Status</c> 文本里统计健康检查结果。
    /// </summary>
    /// <param name="containers">容器列表。</param>
    /// <returns>健康数 / 不健康数。</returns>
    /// <remarks>
    /// <para><b>只能读文本</b>：容器列表没有结构化的健康字段，健康状态只出现在 <c>Status</c>
    /// 的括号里（<c>Up 2 hours (healthy)</c>）。逐个 inspect 才是结构化的，但那是 N 次请求。</para>
    /// <para><b>必须先判 <c>(unhealthy)</c></b>：<c>(healthy)</c> 是 <c>(unhealthy)</c> 的子串，
    /// 反过来的话失败的健康检查会被算成健康——这正是这个功能最不该出的错。</para>
    /// <para>没配健康检查的容器两者都不计入：它的 <c>Status</c> 里没有括号。</para>
    /// </remarks>
    private static (long Healthy, long Unhealthy) CountHealth(IEnumerable<ContainerListResponse> containers)
    {
        long healthy = 0;
        long unhealthy = 0;

        foreach (var container in containers)
        {
            var status = container.Status ?? string.Empty;

            if (status.Contains(HealthUnhealthyMarker, StringComparison.OrdinalIgnoreCase))
            {
                unhealthy++;
            }
            else if (status.Contains(HealthHealthyMarker, StringComparison.OrdinalIgnoreCase))
            {
                healthy++;
            }
        }

        return (healthy, unhealthy);
    }

    /// <summary>
    /// 查询镜像层实际占用的磁盘空间（<c>GET /system/df?type=image</c>）。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="hostName">主机名（仅用于日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>字节数；查询失败或不支持时 <see langword="null"/>（表示「不知道」，不是「0 字节」）。</returns>
    /// <remarks>
    /// <para><b>为什么不走 <see cref="DockerErrorTranslator"/></b>：镜像占用是锦上添花的信息，
    /// 取不到不该把整台主机判成离线（那会让四类资源磁贴一起变成「—」）。这里只吞掉
    /// 「守护进程调用失败」类异常并记 Warning。</para>
    /// <para><b><c>type=image</c> 过滤不可省</b>：不带过滤时守护进程会连带统计容器、卷与构建缓存，
    /// 在镜像多的机器上要多等好几秒。</para>
    /// <para><b>不缓存</b>：概览页是用户主动进来看的，30 秒级别的陈旧没有意义；
    /// 真要压延迟应该在概览整体上加缓存。</para>
    /// </remarks>
    private async Task<long?> TryGetImagesSizeAsync(
        IDockerClient client,
        string hostName,
        CancellationToken cancellationToken)
    {
        try
        {
            var usage = await client.System.GetDataUsageInfoAsync(
                new SytemDataUsageInfoParameters { Type = [ImageUsageType] },
                cancellationToken).ConfigureAwait(false);

            return usage.ImageUsage?.TotalSize;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DockerApiException
            or HttpRequestException
            or SocketException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarning(
                exception,
                "主机 {HostName} 的镜像磁盘占用查询失败：界面将显示为未知",
                hostName);

            return null;
        }
    }

    /// <summary>探测主机连通性。
    /// </summary>
    /// <param name="host">目标主机。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连通状态。</returns>
    /// <remarks>
    /// 用 <c>GetVersionAsync</c> 而不是专门的 ping：多取回的那点信息在概览里正好用得上，
    /// 而它同样是「连得上才算成功」的轻量调用。
    /// </remarks>
    private async Task<HostConnectivityStatus> ProbeAsync(
        DockerHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await _accessor.GetClientAsync(host.Name, cancellationToken).ConfigureAwait(false);

            // 经翻译器归一化后再判类别：否则「连接被拒」与「守护进程返回了怪东西」会落进同一个 catch，
            // 而前者是离线、后者是未知——离线时该去查网络，未知时该去查日志。
            await DockerErrorTranslator.ExecuteAsync(
                () => client.System.GetVersionAsync(cancellationToken),
                $"主机 {host.Name}",
                cancellationToken).ConfigureAwait(false);

            return HostConnectivityStatus.Online;
        }
        catch (BusinessException exception) when (
            exception.Code is ErrorCodes.HostUnreachable or ErrorCodes.HostDisabled)
        {
            return HostConnectivityStatus.Offline;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "探测主机 {HostName} 连通性时失败，状态记为未知", host.Name);

            return HostConnectivityStatus.Unknown;
        }
    }

    /// <summary>
    /// 投影主机概览。
    /// </summary>
    /// <param name="host">主机实体。</param>
    /// <param name="status">连通状态。</param>
    /// <param name="counts">资源计数。</param>
    /// <param name="startedAt">守护进程启动时刻。</param>
    /// <param name="version">版本信息。</param>
    /// <returns>主机概览。</returns>
    private static HostOverviewSummary Map(
        DockerHost host,
        HostConnectivityStatus status,
        HostResourceCounts? counts,
        DateTimeOffset? startedAt,
        HostVersionDetail? version) => new(
            Id: host.Name,
            Name: host.Name,
            Type: host.Type,
            Endpoint: host.Endpoint.Value,
            Enabled: host.Enabled,
            Status: status,
            Counts: counts,
            StartedAt: startedAt,
            Version: version,
            GroupId: host.GroupId);

    /// <summary>
    /// 投影版本信息。
    /// </summary>
    /// <param name="source">SDK 版本响应。</param>
    /// <returns>版本投影。</returns>
    private static HostVersionDetail MapVersion(VersionResponse source) => new(
        Version: source.Version,
        ApiVersion: source.APIVersion,
        Os: source.Os,
        Arch: source.Arch,
        GoVersion: source.GoVersion);

    /// <summary>
    /// 把守护进程的启动时刻文本解析成带时区的时刻。
    /// </summary>
    /// <param name="value">RFC 3339 文本（如 <c>2026-01-02T03:04:05.678901234Z</c>）。</param>
    /// <returns>解析成功时返回该时刻；否则返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 解析失败返回 <see langword="null"/> 而不是抛异常：启动时间只是展示项，
    /// 为了它让整个概览接口失败是本末倒置。
    /// </remarks>
    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}
