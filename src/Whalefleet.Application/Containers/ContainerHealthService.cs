using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 批量查询容器健康与退出原因的用例（<c>POST /api/v1/hosts/{hostId}/containers/health</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要这个端点</b>：容器列表接口（<c>/containers/json</c>）<b>不返回</b>结构化的
/// 健康状态与重启计数——健康只出现在 <c>Status</c> 文本里，重启计数只有 inspect 才有。
/// 列表页要在每一行显示「不健康 / 反复重启」，就只能逐个 inspect。</para>
/// <para><b>代价与保护</b>：这是 N 次调用。因此上限 100（= 列表一页上限）且并发度 8——
/// 串行时 100 个容器在远程主机上要等好几秒；并发太高又会把小型守护进程打满。</para>
/// <para><b>单个容器查不到不算失败</b>：容器可能在两次刷新之间被删掉。
/// 那一条直接缺席，前端显示「—」，而不是让整页变成错误。</para>
/// </remarks>
public sealed class ContainerHealthService
{
    /// <summary>
    /// 单次请求允许查询的容器数上限（与列表分页上限一致）。
    /// </summary>
    public const int MaxContainers = 100;

    /// <summary>
    /// 并发 inspect 的上限。
    /// </summary>
    /// <remarks>
    /// 8 的依据：本机 socket 上几十个容器几乎瞬间完成，而一条走 TCP 的远程主机每秒能稳定吃下十余个请求；
    /// 再高就会在小内存机器上看到守护进程排队、整体反而更慢。
    /// </remarks>
    private const int MaxConcurrency = 8;

    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<ContainerHealthService> _logger;

    /// <summary>
    /// 初始化用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerHealthService(
        IDockerHostAccessor accessor,
        ILogger<ContainerHealthService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// 批量查询容器健康与退出原因。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（容器标识列表）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>查到的容器健康信息（顺序与请求一致）。</returns>
    /// <exception cref="BusinessException">
    /// 容器列表为空 / 超上限（<c>1000</c>）、主机不可达（<c>1301</c>）或已停用（<c>1302</c>）。
    /// </exception>
    public async Task<ContainerHealthResult> GetAsync(
        string hostId,
        ContainerHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var ids = Normalize(request.ContainerIds);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        using var gate = new SemaphoreSlim(MaxConcurrency);

        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var inspect = await client.Containers
                    .InspectContainerAsync(id, cancellationToken)
                    .ConfigureAwait(false);

                var state = inspect.State;
                var running = state?.Running ?? false;

                return new ContainerHealthSummary(
                    ContainerId: inspect.ID ?? id,
                    Name: inspect.Name?.TrimStart('/') ?? string.Empty,
                    HealthStatus: ContainerHealth.FromInspect(state?.Health?.Status),
                    // 重启次数在 inspect 的**根对象**上，不在 State 里
                    RestartCount: inspect.RestartCount,
                    // 运行中的容器 ExitCode 恒为 0，给出去会被读成「已正常退出」
                    ExitCode: state is null || running ? null : state.ExitCode,
                    OomKilled: state?.OOMKilled ?? false,
                    Running: running);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 容器在两次刷新之间被删掉是常态；单条失败不该让整批失败
                _logger.LogDebug(
                    "查询容器 {ContainerId} 的健康信息失败（将在结果中缺席）：{Reason}",
                    id,
                    exception.Message);

                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return new ContainerHealthResult([.. results.OfType<ContainerHealthSummary>()]);
    }

    /// <summary>
    /// 校验并去重容器标识列表。
    /// </summary>
    /// <param name="ids">原始列表。</param>
    /// <returns>去重后的标识列表。</returns>
    /// <exception cref="BusinessException">列表为空、含空白项或超过上限（<c>1000</c>）。</exception>
    private static List<string> Normalize(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            throw BusinessException.Validation("containerIds 不能为空");
        }

        if (ids.Count > MaxContainers)
        {
            throw BusinessException.Validation($"containerIds 一次最多 {MaxContainers} 项");
        }

        var normalized = new List<string>(ids.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw BusinessException.Validation("containerIds 不得包含空白项");
            }

            var trimmed = id.Trim();

            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }
}
