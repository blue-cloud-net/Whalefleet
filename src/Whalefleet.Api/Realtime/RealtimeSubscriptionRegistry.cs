namespace Whalefleet.Api.Realtime;

/// <summary>
/// 一条实时订阅。
/// </summary>
/// <remarks>
/// <para><b>为什么订阅要自己持有取消源</b>：SignalR 的 Hub 实例是<b>每次调用</b>新建的，
/// 方法返回后就被释放。真正长期存在的是这条订阅——它对应一条到 Docker 守护进程的长连接
/// 与一个后台推送任务。因此取消源必须挂在注册表里，而不是挂在 Hub 上。</para>
/// <para><b>为什么泵任务也在这里</b>：断开连接时必须能<b>确定性地</b>停掉它
/// （取消令牌 + 等待任务结束），否则会留下一个还在往已断开的连接推消息的任务，
/// 每秒产生一次「连接已关闭」的异常。</para>
/// </remarks>
public sealed class RealtimeSubscription
{
    /// <summary>
    /// 初始化订阅。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别（日志 / 统计 / 进度）。</param>
    /// <param name="resourceId">资源标识（容器 ID / 进度令牌）。</param>
    public RealtimeSubscription(string connectionId, string kind, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        ConnectionId = connectionId;
        Kind = kind;
        ResourceId = resourceId;
        Cancellation = new CancellationTokenSource();
    }

    /// <summary>连接标识。</summary>
    public string ConnectionId { get; }

    /// <summary>资源类别（日志 / 统计 / 进度）。</summary>
    public string Kind { get; }

    /// <summary>资源标识（容器 ID / 进度令牌）。</summary>
    public string ResourceId { get; }

    /// <summary>取消源（断开或退订时触发）。</summary>
    public CancellationTokenSource Cancellation { get; }

    /// <summary>后台推送任务。</summary>
    public Task? Pump { get; set; }

    /// <summary>
    /// 停止订阅：取消推送并等待任务结束。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task StopAsync()
    {
        await this.Cancellation.CancelAsync().ConfigureAwait(false);

        var pump = this.Pump;

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消导致的结束是预期路径。
            }
            catch (Exception)
            {
                // 泵任务内部已记录日志；这里只保证不让异常冒到断开流程上。
            }
        }

        this.Cancellation.Dispose();
    }
}

/// <summary>
/// 实时订阅注册表（Singleton）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须是 Singleton</b>：订阅的生命周期跨多个 Hub 调用
/// （订阅时登记、退订或断开时移除），而 Hub 实例每次调用都是新的。
/// 注册成 Scoped 的话，每次调用都会拿到一份新的空表，订阅永远登记不上。</para>
/// <para><b>为什么用一把锁而不是并发字典</b>：条目数极少（每连接每类最多 5 个），
/// 而「登记 / 移除 / 计数」这三件事必须<b>原子地</b>完成——
/// 拆成多个并发字典时，计数与条目会漂移（计数只增不减时尤其明显）。
/// 一把锁 + 一个字典就没有这个问题。</para>
/// <para><b>为什么按「连接 + 类别 + 资源」三维登记</b>：同一个连接可能同时订阅多个容器，
/// 而「每连接每类最多 N 个」这条限制需要能<b>按类别计数</b>——
/// 只按连接计数会把日志与统计混在一起限流。</para>
/// </remarks>
public sealed class RealtimeSubscriptionRegistry
{
    /// <summary>
    /// 订阅表（键 = 连接 + 类别 + 资源）。
    /// </summary>
    private readonly Dictionary<string, RealtimeSubscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// 保护订阅表的锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 登记一条订阅。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <param name="maxPerKind">同一连接同一类别的上限。</param>
    /// <returns>登记成功返回订阅；已达上限或已存在返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>重复订阅同一资源直接拒绝</b>：返回 <see langword="null"/> 而不是再开一条——
    /// 否则同一个容器会被开两条 Docker 长连接，那是在替守护进程放大负载。
    /// </remarks>
    public RealtimeSubscription? TryRegister(
        string connectionId,
        string kind,
        string resourceId,
        int maxPerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        lock (this._gate)
        {
            var key = BuildKey(connectionId, kind, resourceId);

            if (this._subscriptions.ContainsKey(key))
            {
                return null;
            }

            if (this.CountPerKindCore(connectionId, kind) >= maxPerKind)
            {
                return null;
            }

            var subscription = new RealtimeSubscription(connectionId, kind, resourceId);
            this._subscriptions[key] = subscription;

            return subscription;
        }
    }

    /// <summary>
    /// 移除一条订阅（不停止推送任务）。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <returns>找到并移除返回 <see langword="true"/>。</returns>
    public bool Remove(string connectionId, string kind, string resourceId)
    {
        lock (this._gate)
        {
            return this._subscriptions.Remove(BuildKey(connectionId, kind, resourceId));
        }
    }

    /// <summary>
    /// 移除并停止一条订阅。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task RemoveAndStopAsync(string connectionId, string kind, string resourceId)
    {
        RealtimeSubscription? removed;

        lock (this._gate)
        {
            this._subscriptions.Remove(BuildKey(connectionId, kind, resourceId), out removed);
        }

        if (removed is not null)
        {
            await removed.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 停止并清空某条连接的全部订阅。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task StopAllAsync(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        List<RealtimeSubscription> removed;

        lock (this._gate)
        {
            removed = [];

            foreach (var key in this._subscriptions.Keys.ToList())
            {
                if (key.StartsWith(string.Concat(connectionId, "|"), StringComparison.Ordinal)
                    && this._subscriptions.Remove(key, out var subscription))
                {
                    removed.Add(subscription);
                }
            }
        }

        foreach (var subscription in removed)
        {
            await subscription.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 某条连接在某类别上的订阅数。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别。</param>
    /// <returns>订阅数。</returns>
    public int CountPerKind(string connectionId, string kind)
    {
        lock (this._gate)
        {
            return this.CountPerKindCore(connectionId, kind);
        }
    }

    /// <summary>
    /// 找出订阅了某个资源的全部连接。
    /// </summary>
    /// <param name="kind">资源类别。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <returns>匹配的订阅列表（快照）。</returns>
    /// <remarks>
    /// 用于「按令牌找订阅者」这类<b>反向</b>查询：进度频道是客户端生成的令牌，
    /// 推送时要反查「谁订阅了这个令牌」，而订阅表是按连接正向建立的。
    /// </remarks>
    public IReadOnlyList<RealtimeSubscription> FindByResource(string kind, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        var suffix = string.Concat('|', kind, '|', resourceId);
        var matches = new List<RealtimeSubscription>();

        lock (this._gate)
        {
            foreach (var pair in this._subscriptions)
            {
                if (pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    matches.Add(pair.Value);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// 统计某条连接在某类别上的订阅数（调用方须已持锁）。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别。</param>
    /// <returns>订阅数。</returns>
    private int CountPerKindCore(string connectionId, string kind)
    {
        var prefix = string.Concat(connectionId, "|", kind, "|");
        var count = 0;

        foreach (var key in this._subscriptions.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 组合订阅键。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="kind">资源类别。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <returns>组合键。</returns>
    private static string BuildKey(string connectionId, string kind, string resourceId) =>
        string.Concat(connectionId, "|", kind, "|", resourceId);
}
