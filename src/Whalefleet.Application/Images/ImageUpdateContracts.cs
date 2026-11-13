namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像自动更新策略（接口投影）。
/// </summary>
/// <param name="Id">策略主键。</param>
/// <param name="ImageReference">镜像引用（<c>仓库:标签</c>）。</param>
/// <param name="Mode">执行模式（<c>notify</c> / <c>auto</c>）。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="State">最近一次检查结论（<c>unknown</c> / <c>up-to-date</c> /
/// <c>update-available</c> / <c>applied</c> / <c>failed</c>）。</param>
/// <param name="BaselineDigest">基线摘要（上次确认过的远端摘要）。</param>
/// <param name="RemoteDigest">最近一次探测到的远端摘要。</param>
/// <param name="Message">结论说明（失败原因等）；可为空。</param>
/// <param name="LastCheckedAt">最近一次检查时间（UTC）。</param>
/// <param name="LastAppliedAt">最近一次自动应用成功时间（UTC）。</param>
public sealed record ImageUpdatePolicyDto(
    Guid Id,
    string ImageReference,
    string Mode,
    bool Enabled,
    string State,
    string? BaselineDigest,
    string? RemoteDigest,
    string? Message,
    DateTime? LastCheckedAt,
    DateTime? LastAppliedAt);

/// <summary>
/// 新增策略请求。
/// </summary>
public sealed record CreateImageUpdatePolicyRequest
{
    /// <summary>
    /// 镜像引用（<c>仓库:标签</c>）；省略标签时取 <c>latest</c>。
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>
    /// 执行模式（<c>notify</c> / <c>auto</c>）；省略时取 <c>notify</c>。
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// 是否启用；省略时启用。
    /// </summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// 修改策略请求（只改执行模式与启用状态）。
/// </summary>
/// <remarks>
/// <b>镜像引用不可改</b>：改引用等于换一条策略（历史结论、基线摘要全都失去意义）。
/// 界面上的正确做法是删掉旧的、新建一条，这样审计里能清楚看到「换过镜像」。
/// </remarks>
public sealed record UpdateImageUpdatePolicyRequest
{
    /// <summary>
    /// 策略主键。
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// 执行模式；省略表示不改。
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// 是否启用；省略表示不改。
    /// </summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// 删除策略请求。
/// </summary>
public sealed record RemoveImageUpdatePolicyRequest
{
    /// <summary>
    /// 策略主键。
    /// </summary>
    public Guid Id { get; init; }
}

/// <summary>
/// 立即检查某个策略的请求。
/// </summary>
public sealed record CheckImageUpdatePolicyRequest
{
    /// <summary>
    /// 策略主键。
    /// </summary>
    public Guid Id { get; init; }
}

/// <summary>
/// 立即检查的结果。
/// </summary>
/// <param name="Policy">检查后的策略（含最新结论）。</param>
/// <param name="RecreatedContainers">本次重建的容器数（仅自动模式可能大于 0）。</param>
public sealed record ImageUpdateCheckResult(
    ImageUpdatePolicyDto Policy,
    int RecreatedContainers);

/// <summary>
/// 一轮扫描的汇总。
/// </summary>
/// <param name="Checked">本轮检查的策略数。</param>
/// <param name="Updated">其中发现新版本的策略数。</param>
/// <param name="Applied">其中完成自动应用的策略数。</param>
/// <param name="Failed">其中检查失败的策略数。</param>
public sealed record ImageUpdateScanSummary(
    int Checked,
    int Updated,
    int Applied,
    int Failed);
