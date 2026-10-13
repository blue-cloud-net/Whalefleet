using System.Globalization;
using Docker.DotNet.Models;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Application.Docker;
using Microsoft.Extensions.Logging;

namespace Docker.Manager.Application.Hosts;

/// <summary>
/// 主机概览查询用例（版本 + 运行摘要），对应 <c>/api/v1/hosts/{hostId}/overview</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个服务而不是并进 <c>HostService</c></b>：那是管理员的主机 CRUD（增删改端点、凭据、
/// 客户端失效），而这里是任意已认证用户都能看的只读信息。混在一起会让「哪些方法需要管理员」
/// 只能靠读方法体去猜。</para>
/// <para><b>主机校验不在这里</b>：主机是否存在（<c>1300</c>）与可见性（<c>1501</c>）由
/// <c>CurrentHostMiddleware</c> 在进入本服务前完成。</para>
/// </remarks>
public sealed class HostOverviewService
{
    private readonly IDockerHostAccessor _accessor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostOverviewService> _logger;

    /// <summary>
    /// 初始化主机概览查询用例。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="timeProvider">时钟（计算已运行时长）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HostOverviewService(
        IDockerHostAccessor accessor,
        TimeProvider timeProvider,
        ILogger<HostOverviewService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 查询主机概览。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>版本信息与运行摘要。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）、已停用（<c>1302</c>）、Docker 调用失败（<c>1402</c>）或超时（<c>1403</c>）。
    /// </exception>
    /// <remarks>
    /// 两次调用<b>串行</b>而不是并行：它们是同一个守护进程上的两次本地调用（毫秒级），
    /// 而并行会让「两次调用各自可能失败」变成「要么两个都成功、要么丢掉另一个的错误信息」，
    /// 排查时反而更费劲。真到了需要压这一步延迟的规模，该做的是缓存，不是并行。
    /// </remarks>
    public async Task<HostOverviewSummary> GetAsync(
        string hostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var version = await DockerErrorTranslator.ExecuteAsync(
            () => client.System.GetVersionAsync(cancellationToken),
            "主机版本信息",
            cancellationToken).ConfigureAwait(false);

        var info = await DockerErrorTranslator.ExecuteAsync(
            () => client.System.GetSystemInfoAsync(cancellationToken),
            "主机运行信息",
            cancellationToken).ConfigureAwait(false);

        var startedAt = ParseTimestamp(info.SystemTime);

        _logger.LogDebug(
            "主机 {HostId} 信息：引擎 {Version}，API {ApiVersion}，容器 {Running}/{Total}",
            hostId,
            version.Version,
            version.APIVersion,
            info.ContainersRunning,
            info.Containers);

        return new HostOverviewSummary(
            MapVersion(version),
            MapRuntime(info, startedAt));
    }

    /// <summary>
    /// 投影版本信息。
    /// </summary>
    /// <param name="source">SDK 版本响应。</param>
    /// <returns>版本投影。</returns>
    private static HostVersionDetail MapVersion(VersionResponse source) => new(
        Version: source.Version ?? string.Empty,
        ApiVersion: source.APIVersion ?? string.Empty,
        MinApiVersion: source.MinAPIVersion ?? string.Empty,
        Os: source.Os ?? string.Empty,
        Arch: source.Arch ?? string.Empty,
        GoVersion: source.GoVersion ?? string.Empty,
        KernelVersion: source.KernelVersion ?? string.Empty,
        Experimental: source.Experimental ?? false);

    /// <summary>
    /// 投影运行摘要。
    /// </summary>
    /// <param name="source">SDK 运行信息响应。</param>
    /// <param name="startedAt">解析出的启动时刻。</param>
    /// <returns>运行摘要投影。</returns>
    private HostRuntimeDetail MapRuntime(SystemInfoResponse source, DateTimeOffset? startedAt) => new(
        Name: source.Name ?? string.Empty,
        ServerVersion: source.ServerVersion ?? string.Empty,
        StartedAt: startedAt,
        // 取不到启动时刻就没有「已运行多久」可言：给 0 会被读成「刚刚启动」。
        UptimeSeconds: startedAt is null
            ? null
            : (long)Math.Max(0, (_timeProvider.GetUtcNow() - startedAt.Value).TotalSeconds),
        Containers: source.Containers,
        ContainersRunning: source.ContainersRunning,
        ContainersPaused: source.ContainersPaused,
        ContainersStopped: source.ContainersStopped,
        Images: source.Images,
        Cpus: source.NCPU,
        MemoryTotalBytes: source.MemTotal,
        OperatingSystem: source.OperatingSystem ?? string.Empty);

    /// <summary>
    /// 把守护进程的启动时刻文本解析成带时区的时刻。
    /// </summary>
    /// <param name="value">RFC 3339 文本（如 <c>2026-01-02T03:04:05.678901234Z</c>）。</param>
    /// <returns>解析成功时返回该时刻；否则返回 <see langword="null"/>。</returns>
    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}
