namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排任务的状态。
/// </summary>
/// <remarks>
/// <b>为什么是枚举而不是字符串</b>：状态是<b>封闭集合</b>（就这四种），
/// 不像审计动作那样会随功能增加——因此用枚举能在编译期穷举检查。
/// 对外序列化时转小写文本（见 <see cref="StackTaskStatusText"/>）。
/// </remarks>
public enum StackTaskStatus
{
    /// <summary>
    /// 已入队，尚未开始执行。
    /// </summary>
    Queued,

    /// <summary>
    /// 正在执行。
    /// </summary>
    Running,

    /// <summary>
    /// 已成功完成。
    /// </summary>
    Succeeded,

    /// <summary>
    /// 已失败（<see cref="StackTaskSnapshot.ErrorCode"/> 给出原因码）。
    /// </summary>
    Failed,
}

/// <summary>
/// 编排任务状态的文本常量。
/// </summary>
/// <remarks>
/// <b>不用 <c>Enum.ToString()</c> 直接产出</b>：那会得到 <c>Queued</c> 这类 PascalCase，
/// 与本项目其它文本约定（错误码、权限名一律小写）不一致。
/// </remarks>
public static class StackTaskStatusText
{
    /// <summary>
    /// 已入队。
    /// </summary>
    public const string Queued = "queued";

    /// <summary>
    /// 正在执行。
    /// </summary>
    public const string Running = "running";

    /// <summary>
    /// 已成功完成。
    /// </summary>
    public const string Succeeded = "succeeded";

    /// <summary>
    /// 已失败。
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// 状态 → 文本。
    /// </summary>
    /// <param name="status">状态。</param>
    /// <returns>小写文本。</returns>
    public static string From(StackTaskStatus status) => status switch
    {
        StackTaskStatus.Queued => Queued,
        StackTaskStatus.Running => Running,
        StackTaskStatus.Succeeded => Succeeded,
        StackTaskStatus.Failed => Failed,
        _ => Queued,
    };
}

/// <summary>
/// 编排操作的结果摘要。
/// </summary>
/// <param name="Created">新建的服务数。</param>
/// <param name="Recreated">因配置变化而重建的服务数。</param>
/// <param name="Removed">被清掉的孤儿服务数。</param>
/// <param name="SkippedOrphans">被保留（用户选择不清）的孤儿服务数。</param>
/// <param name="Started">启动的容器数。</param>
/// <param name="Stopped">停止的容器数。</param>
/// <param name="NetworksCreated">新建的网络数。</param>
/// <param name="VolumesCreated">新建的卷数。</param>
/// <param name="VolumesRemoved">删除的卷数（只有显式要求时才非零）。</param>
public sealed record StackOperationResult(
    int Created,
    int Recreated,
    int Removed,
    int SkippedOrphans,
    int Started,
    int Stopped,
    int NetworksCreated,
    int VolumesCreated,
    int VolumesRemoved);

/// <summary>
/// 编排任务的快照（对外形态，不可变）。
/// </summary>
/// <param name="TaskId">任务标识（客户端生成的进度令牌）。</param>
/// <param name="HostName">所属主机名。</param>
/// <param name="StackName">Stack 名称。</param>
/// <param name="Operation">操作类型（<c>deploy</c> / <c>update</c> / <c>stop</c> / <c>remove</c>）。</param>
/// <param name="Status">状态。</param>
/// <param name="Percent">总体进度百分比（0–100）；无法确定时为 <see langword="null"/>（不是 0）。</param>
/// <param name="Step">当前步骤的简短描述。</param>
/// <param name="ErrorCode">失败时的业务错误码。</param>
/// <param name="ErrorMessage">失败原因。</param>
/// <param name="Warnings">警告（如解析期的提示）。</param>
/// <param name="Result">成功时的结果摘要。</param>
/// <param name="CreatedAt">创建时刻（UTC）。</param>
/// <param name="UpdatedAt">最后更新时刻（UTC）。</param>
public sealed record StackTaskSnapshot(
    string TaskId,
    string HostName,
    string StackName,
    string Operation,
    string Status,
    double? Percent,
    string? Step,
    int? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string> Warnings,
    StackOperationResult? Result,
    DateTime CreatedAt,
    DateTime UpdatedAt);
