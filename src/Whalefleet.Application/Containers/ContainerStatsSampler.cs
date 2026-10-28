using System.Diagnostics;
using System.Threading.Channels;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器资源采样的共享实现（进程内单例）。
/// </summary>
/// <remarks>
/// <para><b>引用计数 + 宽限期</b>：第一个订阅者到来时起泵，最后一个订阅者离开后<b>不立刻</b>停泵，
/// 而是等 <see cref="RealtimeOptions.StatsSamplerIdleGraceSeconds"/>。理由是界面上的订阅天然抖动
/// （切换列、翻页、路由跳转都会先退订再订阅），立刻停泵会让每次抖动都重开一条 Docker 流，
/// 采样也从零开始攒——sparkline 会一直闪成空图。</para>
/// <para><b>一把锁管全部状态</b>：状态变更（订阅 / 退订 / 发布 / 清理）都是短临界区，
/// 而发布频率上限是「容器数 × 每秒一帧」。为它设计分片锁只会让「订阅与快照要么都发生、
/// 要么都不发生」这条不变量变得难以证明。</para>
/// <para><b>环形缓冲只存最近 N 点</b>：足够画两分钟的 sparkline，也不会让长跑进程越吃越多内存。</para>
/// </remarks>
public sealed class ContainerStatsSampler : IContainerStatsSampler
{
    /// <summary>
    /// 单个订阅者的实时频道容量（帧）。满了丢最旧的。
    /// </summary>
    /// <remarks>
    /// 客户端跟不上时（页面卡顿、网络抖动）宁可丢帧也不要阻塞发布：
    /// 发布端是 Docker 的读取回调，阻塞它等于停掉整条流的读取。
    /// </remarks>
    private const int SubscriberChannelCapacity = 8;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<ContainerStatsSampler> _logger;

    /// <summary>
    /// 初始化采样器。
    /// </summary>
    /// <param name="scopeFactory">作用域工厂（泵任务需要 scoped 的 <see cref="IContainerStatsProvider"/>）。</param>
    /// <param name="options">实时通道选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerStatsSampler(
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeOptions> options,
        ILogger<ContainerStatsSampler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public ContainerStatsSubscription Subscribe(string hostId, string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var key = KeyOf(hostId, containerId);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(hostId, containerId);
                _entries[key] = entry;
            }

            // 取消可能挂着的「宽限期停泵」：新的订阅已经把它救回来了
            entry.Generation++;

            var channel = Channel.CreateBounded<ContainerStatsSample>(
                new BoundedChannelOptions(SubscriberChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

            entry.Subscribers.Add(channel);

            // 快照与「挂上频道」在同一临界区：历史与实时既不重叠也不留缝
            var history = entry.Buffer.ToArray();

            if (entry.PumpTask is null or { IsCompleted: true })
            {
                entry.PumpTask = this.StartPump(key, entry);
            }

            return new ContainerStatsSubscription(
                () => this.ReleaseAsync(key, channel),
                channel,
                history);
        }
    }

    /// <summary>
    /// 退订：从订阅者集合里移除，最后一个离开时安排宽限期停泵。
    /// </summary>
    /// <param name="key">内部键。</param>
    /// <param name="channel">要移除的频道。</param>
    /// <returns>表示异步操作的任务。</returns>
    private ValueTask ReleaseAsync(string key, Channel<ContainerStatsSample> channel)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return ValueTask.CompletedTask;
            }

            entry.Subscribers.Remove(channel);
            channel.Writer.TryComplete();

            if (entry.Subscribers.Count == 0)
            {
                this.ScheduleIdleStop(key, entry);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 安排「宽限期后停泵」。
    /// </summary>
    /// <param name="key">内部键。</param>
    /// <param name="entry">当时的条目（用于识别「期间又被复用」）。</param>
    /// <remarks>
    /// 用 <see cref="Entry.Generation"/> 做代际校验：宽限期内若来了新订阅者，
    /// 它会把 Generation +1，本任务醒来时发现代际变了就直接放弃。
    /// </remarks>
    private void ScheduleIdleStop(string key, Entry entry)
    {
        var generation = ++entry.Generation;
        var grace = TimeSpan.FromSeconds(_options.Value.StatsSamplerIdleGraceSeconds);
        var token = entry.Cancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(grace, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                // 期间可能已经被新条目替换（旧的被清理后同名重建）
                if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                {
                    return;
                }

                if (current.Subscribers.Count > 0 || current.Generation != generation)
                {
                    return;
                }

                current.Cancellation.Cancel();
                current.Buffer.Clear();
                _entries.Remove(key);
            }

            _logger.LogDebug("容器 {ContainerId} 的采样在空闲宽限期后停止", entry.ContainerId);
        });
    }

    /// <summary>
    /// 起泵：从 Docker 读采样，按节流节奏发布给全部订阅者。
    /// </summary>
    /// <param name="key">内部键。</param>
    /// <param name="entry">条目。</param>
    /// <returns>泵任务。</returns>
    private Task StartPump(string key, Entry entry)
    {
        var hostId = entry.HostId;
        var containerId = entry.ContainerId;
        var token = entry.Cancellation.Token;
        var sampleInterval = TimeSpan.FromMilliseconds(_options.Value.StatsSampleIntervalMs);
        var historyCapacity = _options.Value.StatsHistoryPoints;
        var scopeFactory = _scopeFactory;
        var logger = _logger;

        _logger.LogDebug("开始采样容器 {ContainerId}（主机 {HostId}）", containerId, hostId);

        return Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IContainerStatsProvider>();

            var clock = Stopwatch.StartNew();

            // ⚠️ 「还没发布过」不能用 TimeSpan.MinValue 表示：
            // `clock.Elapsed - TimeSpan.MinValue` 会越过 TimeSpan 的表示范围，
            // **在第一帧上**就抛 OverflowException——而第一帧恰好是整条链路上
            // 最先被消费的那一帧。症状是「订阅成功、后端日志一条警告、
            // 界面永远停在『等待第一份采样…』」，且没有任何一条错误能指向真正的原因。
            // 用可空值表示「尚未发布」，首帧因此立即发布（而不是被节流丢掉）。
            TimeSpan? lastPublish = null;

            try
            {
                await foreach (var sample in provider
                    .StreamStatsAsync(hostId, containerId, token)
                    .ConfigureAwait(false))
                {
                    // 节流：Docker 本身约 1 秒一帧，但不同守护进程实现有差异。
                    // 客户端只画两分钟内的曲线，推得更密并不会更有用。
                    var now = clock.Elapsed;

                    if (lastPublish is { } last && now - last < sampleInterval)
                    {
                        continue;
                    }

                    lastPublish = now;

                    this.Publish(key, sample, historyCapacity);
                }
            }
            catch (OperationCanceledException)
            {
                // 停止采样：正常路径
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "容器 {ContainerId} 的采样流异常结束，订阅者将收不到新数据",
                    containerId);

                // 把失败**传递给订阅者**：频道以异常完结，订阅方的 `await foreach`
                // 会抛出这个异常。若不传，订阅方会一直阻塞在「等下一帧」上——
                // 界面表现为「等待第一份采样…」转圈到天荒地老，而后端只有一条日志。
                this.FailSubscribers(key, exception);
            }
            finally
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    {
                        current.PumpTask = null;

                        // 没人看着就把条目收掉，好让下次订阅重新起泵（可能是主机恢复了）
                        if (current.Subscribers.Count == 0)
                        {
                            current.Cancellation.Cancel();
                            current.Buffer.Clear();
                            _entries.Remove(key);
                        }
                    }
                }
            }
        });
    }

    /// <summary>
    /// 告诉所有订阅者「这条流出事了」。
    /// </summary>
    /// <param name="key">内部键。</param>
    /// <param name="exception">失败原因。</param>
    /// <remarks>
    /// 用「带异常的完结」而不是「静默地不再发帧」：<c>Channel</c> 会先把已缓冲的帧
    /// 交付完，再把异常抛给读者——订阅方因此既拿到了已有的数据，也知道了失败。
    /// </remarks>
    private void FailSubscribers(string key, Exception exception)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            foreach (var subscriber in entry.Subscribers)
            {
                subscriber.Writer.TryComplete(exception);
            }
        }
    }

    /// <summary>
    /// 发布一条采样：写环形缓冲，再写给每个订阅者。
    /// </summary>
    /// <param name="key">内部键。</param>
    /// <param name="sample">采样。</param>
    /// <param name="historyCapacity">环形缓冲容量。</param>
    private void Publish(string key, ContainerStatsSample sample, int historyCapacity)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.Buffer.Add(sample);

            if (entry.Buffer.Count > historyCapacity)
            {
                // 每次只多一条，RemoveAt(0) 的 O(n) 在 n=60 时完全可以忽略
                entry.Buffer.RemoveAt(0);
            }

            foreach (var subscriber in entry.Subscribers)
            {
                subscriber.Writer.TryWrite(sample);
            }
        }
    }

    /// <summary>
    /// 拼内部键。
    /// </summary>
    /// <param name="hostId">主机名。</param>
    /// <param name="containerId">容器标识。</param>
    /// <returns>内部键。</returns>
    /// <remarks>
    /// 主机名<b>不区分大小写</b>（与 <c>hostId</c> 的既有口径一致），容器标识按原样比较。
    /// 用 <c>\u001f</c> 作分隔符：它不可能出现在主机名或容器 ID 里。
    /// </remarks>
    private static string KeyOf(string hostId, string containerId) =>
        string.Concat(hostId.ToLowerInvariant(), "\u001f", containerId);

    /// <summary>
    /// 单个「主机 + 容器」的采样状态。
    /// </summary>
    private sealed class Entry(string hostId, string containerId)
    {
        /// <summary>主机名。</summary>
        public string HostId { get; } = hostId;

        /// <summary>容器标识。</summary>
        public string ContainerId { get; } = containerId;

        /// <summary>停泵用的取消源。</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>环形缓冲（旧 → 新）。</summary>
        public List<ContainerStatsSample> Buffer { get; } = [];

        /// <summary>当前订阅者。</summary>
        public HashSet<Channel<ContainerStatsSample>> Subscribers { get; } = [];

        /// <summary>泵任务；未起泵或已结束时为 <see langword="null"/>。</summary>
        public Task? PumpTask { get; set; }

        /// <summary>代际号（宽限期停泵任务据此判断「期间是否被复用」）。</summary>
        public int Generation { get; set; }
    }
}
