using System.Collections.Concurrent;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排任务登记表：记住「有哪些任务在跑、跑到哪一步了」。
/// </summary>
/// <remarks>
/// <para><b>为什么放内存而不是建表</b>：任务登记表服务的是「一次操作的状态」，
/// 而操作本身的状态源是<b>守护进程</b>（标签 + 容器）。
/// 登记表只回答「刚才那次请求跑到哪了」——
/// 而这个问题的有效期就是几分钟（用户在等结果）。
/// 落库会带来两个新问题：过期数据要清理、重启后要恢复半截状态。</para>
/// <para><b>重启丢任务的后果可接受</b>：进程重启时正在跑的编排本来就断了，
/// 容器停在「建了一半」的状态；下次打开列表页，界面会按标签把实际情况显示出来
/// （哪些服务在跑、哪些没起来），用户据此重新「应用」即可。</para>
/// <para><b>为什么限制「同一 Stack 同时只有一个任务」</b>：部署 / 更新 / 停止 / 删除
/// 都是「先停后建」的整套动作，两个重叠执行会互相踩——
/// 一个在删容器，另一个在建同名容器。因此登记时就要挡住。</para>
/// <para><b>线程安全</b>：读多写少，但读写来自不同线程（HTTP 请求线程写、后台工作线程改），
/// 因此用 <see cref="ConcurrentDictionary{TKey, TValue}"/> + 锁保护「检查并登记」这一原子步骤。</para>
/// </remarks>
public sealed class StackTaskRegistry
{
    /// <summary>登记表里最多保留的任务数。</summary>
    /// <remarks>
    /// 超出时淘汰最旧的<b>已结束</b>任务。正在跑的任务不淘汰——
    /// 淘汰掉等于让客户端永远等不到结果。
    /// </remarks>
    public const int MaxTasks = 100;

    /// <summary>任务的存活时长（超过则被清理）。</summary>
    public static readonly TimeSpan TaskTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, StackTask> _tasks = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// 登记一个新任务。
    /// </summary>
    /// <param name="taskId">任务标识（客户端生成的进度令牌）。</param>
    /// <param name="hostName">所属主机名。</param>
    /// <param name="stackName">Stack 名称。</param>
    /// <param name="operation">操作类型。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <returns>登记成功返回 <see langword="true"/>。</returns>
    /// <exception cref="BusinessException">
    /// 同一 Stack 已有进行中的任务（<c>1905</c>），或任务标识已被占用（<c>1000</c>）。
    /// </exception>
    public bool TryRegister(
        string taskId,
        string hostName,
        string stackName,
        string operation,
        DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            this.Cleanup(now);

            if (_tasks.ContainsKey(taskId))
            {
                throw new BusinessException(
                    ErrorCodes.ValidationFailed,
                    $"任务标识「{taskId}」已被占用，请换一个");
            }

            var busy = _tasks.Values.Any(task =>
                task.IsTerminal
                    ? false
                    : string.Equals(task.HostName, hostName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(task.StackName, stackName, StringComparison.Ordinal));

            if (busy)
            {
                throw new BusinessException(
                    ErrorCodes.StackOperationInProgress,
                    $"Stack「{stackName}」已有进行中的操作，请等它结束");
            }

            _tasks[taskId] = new StackTask(taskId, hostName, stackName, operation, now);

            return true;
        }
    }

    /// <summary>
    /// 取任务快照。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <returns>快照；不存在返回 <see langword="null"/>。</returns>
    public StackTaskSnapshot? Get(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        return _tasks.TryGetValue(taskId, out var task) ? task.Snapshot() : null;
    }

    /// <summary>
    /// 任务是否存在。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    public bool Contains(string taskId) =>
        !string.IsNullOrWhiteSpace(taskId) && _tasks.ContainsKey(taskId);

    /// <summary>
    /// 撤销一个<b>尚未开始执行</b>的任务登记。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <returns>撤销成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para><b>为什么需要它</b>：登记是「这个 Stack 已被占住」的原子声明，
    /// 而登记之后还有几步可能失败（写库、参数校验）。
    /// 若失败后不撤销，这个 Stack 会被「占住」到 TTL 结束
    /// （默认 1 小时），用户看着「已有进行中的操作」却根本没有任何任务在跑——
    /// 而真正的原因（写库失败）早就被回滚掉了。</para>
    /// <para><b>只撤销未开始的任务</b>：已经开始执行的任务不能撤销，
    /// 否则工作器做完之后写不回状态，任务就永远停在「queued」。</para>
    /// </remarks>
    public bool Abort(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return false;
            }

            if (task.IsTerminal || task.Status != StackTaskStatus.Queued)
            {
                return false;
            }

            return _tasks.TryRemove(taskId, out _);
        }
    }

    /// <summary>
    /// 取全部任务的快照。
    /// </summary>
    /// <returns>快照列表（按创建时间升序）。</returns>
    /// <remarks>
    /// 供「这个 Stack 是不是还有任务在跑」的判定使用——
    /// 那种判定需要看到全部任务，而不只是某一个。
    /// </remarks>
    public IReadOnlyList<StackTaskSnapshot> GetSnapshot()
    {
        lock (_gate)
        {
            return [.. _tasks.Values
                .OrderBy(task => task.CreatedAt)
                .Select(static task => task.Snapshot())];
        }
    }

    /// <summary>
    /// 标记任务开始执行。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="step">当前步骤描述。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkRunning(string taskId, string? step, DateTime now)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.MarkRunning(step, now);
        }
    }

    /// <summary>
    /// 汇报任务进度。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="percent">总体进度百分比；无法确定时为 <see langword="null"/>。</param>
    /// <param name="step">当前步骤描述。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void ReportProgress(string taskId, double? percent, string? step, DateTime now)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.ReportProgress(percent, step, now);
        }
    }

    /// <summary>
    /// 标记任务成功。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="result">结果摘要。</param>
    /// <param name="warnings">警告。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkSucceeded(
        string taskId,
        StackOperationResult result,
        IReadOnlyList<string> warnings,
        DateTime now)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.MarkSucceeded(result, warnings, now);
        }
    }

    /// <summary>
    /// 标记任务失败。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="errorCode">业务错误码。</param>
    /// <param name="errorMessage">失败原因。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    public void MarkFailed(string taskId, int errorCode, string errorMessage, DateTime now)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.MarkFailed(errorCode, errorMessage, now);
        }
    }

    /// <summary>
    /// 清理过期任务。
    /// </summary>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <remarks>
    /// <b>只清已结束的任务</b>：正在跑的任务即使超过 TTL 也不清——
    /// 清掉它等于让客户端永远等不到结果，而它其实还在正常执行。
    /// </remarks>
    private void Cleanup(DateTime now)
    {
        var expired = _tasks
            .Where(pair => pair.Value.IsTerminal && now - pair.Value.UpdatedAt > TaskTtl)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in expired)
        {
            _tasks.TryRemove(key, out _);
        }

        if (_tasks.Count <= MaxTasks)
        {
            return;
        }

        // 超出上限时淘汰最旧的已结束任务；正在跑的一律保留
        var finished = _tasks
            .Where(pair => pair.Value.IsTerminal)
            .OrderBy(pair => pair.Value.UpdatedAt)
            .Take(_tasks.Count - MaxTasks)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in finished)
        {
            _tasks.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// 一个编排任务的可变状态。
/// </summary>
/// <remarks>
/// <b>字段加锁保护</b>：读发生在 HTTP 请求线程（轮询），写发生在后台工作线程。
/// 用锁而不是 volatile，是因为「状态 + 百分比 + 步骤」要一起被读到，
/// 分开读会拿到互相矛盾的组合（如「已成功」但百分比还是 50）。
/// </remarks>
internal sealed class StackTask
{
    private readonly object _gate = new();

    /// <summary>
    /// 初始化任务。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="hostName">所属主机名。</param>
    /// <param name="stackName">Stack 名称。</param>
    /// <param name="operation">操作类型。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    internal StackTask(string taskId, string hostName, string stackName, string operation, DateTime now)
    {
        TaskId = taskId;
        HostName = hostName;
        StackName = stackName;
        Operation = operation;
        Status = StackTaskStatus.Queued;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>任务标识。</summary>
    internal string TaskId { get; }

    /// <summary>所属主机名。</summary>
    internal string HostName { get; }

    /// <summary>Stack 名称。</summary>
    internal string StackName { get; }

    /// <summary>操作类型。</summary>
    internal string Operation { get; }

    /// <summary>状态。</summary>
    internal StackTaskStatus Status { get; private set; }

    /// <summary>总体进度百分比；无法确定时为 <see langword="null"/>。</summary>
    internal double? Percent { get; private set; }

    /// <summary>当前步骤描述。</summary>
    internal string? Step { get; private set; }

    /// <summary>失败时的业务错误码。</summary>
    internal int? ErrorCode { get; private set; }

    /// <summary>失败原因。</summary>
    internal string? ErrorMessage { get; private set; }

    /// <summary>警告。</summary>
    internal IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>成功时的结果摘要。</summary>
    internal StackOperationResult? Result { get; private set; }

    /// <summary>创建时刻（UTC）。</summary>
    internal DateTime CreatedAt { get; }

    /// <summary>最后更新时刻（UTC）。</summary>
    internal DateTime UpdatedAt { get; private set; }

    /// <summary>任务是否已结束。</summary>
    internal bool IsTerminal => Status is StackTaskStatus.Succeeded or StackTaskStatus.Failed;

    /// <summary>
    /// 标记开始执行。
    /// </summary>
    /// <param name="step">当前步骤。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    internal void MarkRunning(string? step, DateTime now)
    {
        lock (_gate)
        {
            Status = StackTaskStatus.Running;
            Step = step;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// 汇报进度。
    /// </summary>
    /// <param name="percent">总体进度百分比。</param>
    /// <param name="step">当前步骤。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    internal void ReportProgress(double? percent, string? step, DateTime now)
    {
        lock (_gate)
        {
            Percent = percent;
            Step = step;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// 标记成功。
    /// </summary>
    /// <param name="result">结果摘要。</param>
    /// <param name="warnings">警告。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    internal void MarkSucceeded(
        StackOperationResult result,
        IReadOnlyList<string> warnings,
        DateTime now)
    {
        lock (_gate)
        {
            Status = StackTaskStatus.Succeeded;
            Result = result;
            Warnings = warnings;
            Percent = 100;
            Step = null;
            ErrorCode = null;
            ErrorMessage = null;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// 标记失败。
    /// </summary>
    /// <param name="errorCode">业务错误码。</param>
    /// <param name="errorMessage">失败原因。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    internal void MarkFailed(int errorCode, string errorMessage, DateTime now)
    {
        lock (_gate)
        {
            Status = StackTaskStatus.Failed;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// 取快照。
    /// </summary>
    /// <returns>任务的不可变快照。</returns>
    internal StackTaskSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new StackTaskSnapshot(
                TaskId,
                HostName,
                StackName,
                Operation,
                StackTaskStatusText.From(Status),
                Percent,
                Step,
                ErrorCode,
                ErrorMessage,
                Warnings,
                Result,
                CreatedAt,
                UpdatedAt);
        }
    }
}
