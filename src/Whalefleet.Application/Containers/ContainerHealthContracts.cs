namespace Whalefleet.Application.Containers;

/// <summary>
/// 批量查询容器健康与退出原因的请求契约。
/// </summary>
/// <remarks>
/// <para><b>为什么要容器标识列表</b>：列表页每次刷新只关心<b>当前页</b>的那几十个容器。
/// 让服务端「自己把全部容器查一遍」会让 1000 个容器的机器为了一次列表刷新做 1000 次 inspect。</para>
/// <para><b>上限 100</b>：与列表分页上限一致，也就等于「一页最多查 100 个」。</para>
/// </remarks>
public sealed record ContainerHealthRequest
{
    /// <summary>容器标识列表（完整 ID / 短 ID / 名称均可）。</summary>
    public IReadOnlyList<string>? ContainerIds { get; init; }
}

/// <summary>
/// 单个容器的健康与退出原因。
/// </summary>
/// <param name="ContainerId">容器 ID（inspect 返回的完整 ID）。</param>
/// <param name="Name">容器名（无前导斜杠）。</param>
/// <param name="HealthStatus">健康检查状态；未配置健康检查为 <see cref="ContainerHealthStatus.None"/>。</param>
/// <param name="RestartCount">守护进程累计的重启次数。</param>
/// <param name="ExitCode">退出码；运行中为 <see langword="null"/>（运行中的 0 会被读成「正常退出」）。</param>
/// <param name="OomKilled">上一次退出是否被 OOM 杀死。</param>
/// <param name="Running">是否正在运行。</param>
public sealed record ContainerHealthSummary(
    string ContainerId,
    string Name,
    ContainerHealthStatus HealthStatus,
    long RestartCount,
    long? ExitCode,
    bool OomKilled,
    bool Running);

/// <summary>
/// 批量查询的结果。
/// </summary>
/// <param name="Items">查到健康信息的容器（顺序与请求一致）。</param>
/// <remarks>
/// <b>查不到的容器直接不出现在 <see cref="Items"/> 里</b>：容器可能在两次刷新之间被删掉了，
/// 那是列表页的正常竞态，不该让整批请求失败。前端对缺失项显示「—」。
/// </remarks>
public sealed record ContainerHealthResult(IReadOnlyList<ContainerHealthSummary> Items);
