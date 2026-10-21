using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机磁盘用量用例（<c>POST /api/v1/hosts/{hostId}/system/df</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个服务</b>：概览只用得上「镜像总占用」这一个数字，
/// 而这里是四类资源的完整明细。塞进 <see cref="HostQueryService"/> 会让概览路径
/// 多背一堆用不到的映射代码，也会让「概览慢」与「用量慢」这两件事难以分开优化。</para>
/// <para><b><c>type</c> 过滤不能省</b>：不带过滤时守护进程会连带统计镜像层、
/// 容器可写层、卷与构建缓存，在镜像多的机器上要多等好几秒（实测本机 216 个卷 3.7 秒，
/// 而 <c>/volumes</c> 只要 1 毫秒）。</para>
/// <para><b>失败即失败</b>：这里是用户主动点开「磁盘用量」才走的路径，
/// 调不通应当报出来（<c>1301</c>），而不是像概览那样降级成 <see langword="null"/>——
/// 后者会让界面显示一片「—」，看不出是主机不可达还是真没数据。</para>
/// </remarks>
public sealed class HostDiskUsageService
{
    /// <summary>
    /// <c>type</c> 的合法取值（Docker Engine API 的原词，<c>build-cache</c> 带连字符）。
    /// </summary>
    private static readonly string[] _allowedTypes = ["image", "container", "volume", "build-cache"];

    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<HostDiskUsageService> _logger;

    /// <summary>
    /// 初始化磁盘用量用例。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HostDiskUsageService(
        IDockerHostAccessor accessor,
        ILogger<HostDiskUsageService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// 查询磁盘用量。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="type">
    /// 只要某一类资源（<c>image</c> / <c>container</c> / <c>volume</c> / <c>build-cache</c>）；
    /// 省略表示四类全要。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用量明细；未查询的类为 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">
    /// <paramref name="type"/> 非法（<c>1000</c>）、主机不可达（<c>1301</c>）或已停用（<c>1302</c>）。
    /// </exception>
    public async Task<HostDiskUsage> GetAsync(
        string hostId,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var types = NormalizeTypes(type);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var usage = await DockerErrorTranslator.ExecuteAsync(
            () => client.System.GetDataUsageInfoAsync(
                new SytemDataUsageInfoParameters { Type = types },
                cancellationToken),
            $"主机 {hostId} 的磁盘用量",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "主机 {HostId} 磁盘用量：镜像 {Images}，容器 {Containers}，卷 {Volumes}，构建缓存 {BuildCache}",
            hostId,
            usage.ImageUsage?.TotalSize,
            usage.ContainerUsage?.TotalSize,
            usage.VolumeUsage?.TotalSize,
            usage.BuildCacheUsage?.TotalSize);

        return new HostDiskUsage(
            Images: MapImages(usage.ImageUsage),
            Containers: MapContainers(usage.ContainerUsage),
            Volumes: MapVolumes(usage.VolumeUsage),
            BuildCache: MapBuildCache(usage.BuildCacheUsage));
    }

    /// <summary>
    /// 校验并规范化 <c>type</c> 参数。
    /// </summary>
    /// <param name="type">原始取值。</param>
    /// <returns>要下发给守护进程的类型列表；空数组表示四类全要。</returns>
    /// <exception cref="BusinessException">取值不在白名单内（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>非法值直接报错</b>而不是「忽略后查全部」：那会让用户以为筛选生效了，
    /// 而实际看到的是四类合计。
    /// </remarks>
    private static string[] NormalizeTypes(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return [];
        }

        var trimmed = type.Trim().ToLowerInvariant();

        if (!_allowedTypes.Contains(trimmed, StringComparer.Ordinal))
        {
            throw BusinessException.Validation(
                $"type「{type}」不是合法取值（{string.Join(" / ", _allowedTypes)}）");
        }

        return [trimmed];
    }

    private static DiskUsageBucket? MapImages(ImageDiskUsage? usage) => usage is null
        ? null
        : new DiskUsageBucket(
            TotalCount: usage.TotalCount,
            ActiveCount: usage.ActiveCount,
            TotalSize: usage.TotalSize,
            Reclaimable: usage.Reclaimable,
            Items: usage.Items is { } items
                ? [.. items.Select(static image => new DiskUsageItem(
                    Id: image.ID,
                    Name: image.RepoTags is { Count: > 0 } tags ? tags[0] : null,
                    Size: image.Size,
                    // 镜像被至少一个容器引用即算「在用」——清理入口据此区分「能删」与「删了会影响容器」
                    InUse: image.Containers > 0))]
                : []);

    private static DiskUsageBucket? MapContainers(ContainerDiskUsage? usage) => usage is null
        ? null
        : new DiskUsageBucket(
            TotalCount: usage.TotalCount,
            ActiveCount: usage.ActiveCount,
            TotalSize: usage.TotalSize,
            Reclaimable: usage.Reclaimable,
            Items: usage.Items is { } items
                ? [.. items.Select(static container => new DiskUsageItem(
                    Id: container.ID,
                    Name: container.Names?.FirstOrDefault()?.TrimStart('/'),
                    // 容器列表响应里的可写层大小要请求时带 size=true 才可能给出
                    Size: container.SizeRw,
                    // 容器自身占着镜像，不存在「无引用」的情形
                    InUse: true))]
                : []);

    private static DiskUsageBucket? MapVolumes(VolumeDiskUsage? usage) => usage is null
        ? null
        : new DiskUsageBucket(
            TotalCount: usage.TotalCount,
            ActiveCount: usage.ActiveCount,
            TotalSize: usage.TotalSize,
            Reclaimable: usage.Reclaimable,
            Items: usage.Items is { } items
                ? [.. items.Select(static volume => new DiskUsageItem(
                    Id: volume.Name,
                    Name: volume.Name,
                    Size: volume.UsageData?.Size,
                    // RefCount 为 0 才是「没人用」；拿不到 UsageData 时给 null（不知道）
                    InUse: volume.UsageData is { } data ? data.RefCount > 0 : null))]
                : []);

    private static DiskUsageBucket? MapBuildCache(BuildDiskUsage? usage) => usage is null
        ? null
        : new DiskUsageBucket(
            TotalCount: usage.TotalCount,
            ActiveCount: usage.ActiveCount,
            TotalSize: usage.TotalSize,
            Reclaimable: usage.Reclaimable,
            Items: usage.Items is { } items
                ? [.. items.Select(static record => new DiskUsageItem(
                    Id: record.ID,
                    Name: record.Description,
                    Size: record.Size,
                    InUse: record.InUse))]
                : []);
}
