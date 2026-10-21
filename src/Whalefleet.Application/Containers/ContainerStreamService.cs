using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器日志流与资源采样流。
/// </summary>
/// <remarks>
/// <para><b>为什么不放进 <see cref="ContainerService"/></b>：那个类是「请求—响应」型用例
/// （一次调用返回一份结果），而本类是「长连接」型——它的生命周期由订阅者的连接决定，
/// 与 HTTP 请求无关。混在一起会让一个类同时有两种截然不同的生命周期语义，
/// 而这正是最容易出错的地方。</para>
/// <para><b>为什么用异步枚举而不是回调</b>：日志是持续产生的，
/// 异步枚举让调用方（Hub 的推送泵）可以逐行消费、随时取消，
/// 而不用自己维护「什么时候结束」的协议。</para>
/// <para><b>关于 Docker SDK 的两条实测结论</b>（改动前务必重读）：
/// <list type="number">
/// <item><description><c>GetContainerLogsAsync</c> 的 <see cref="IProgress{T}"/> 回调
/// <b>逐行触发，且不带行尾换行符</b>。见 <see cref="ContainerLogParser"/>。</description></item>
/// <item><description>该调用返回的 Task 在<b>流结束时</b>才完成（不是「建立连接后」）。
/// 因此它必须<b>不能被 await</b>——await 它等于把整个枚举卡到容器停止为止。</description></item>
/// </list></para>
/// </remarks>
public sealed class ContainerStreamService : IContainerLogProvider, IContainerStatsProvider
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<ContainerStreamService> _logger;
    private readonly RealtimeOptions _options;

    /// <summary>
    /// 初始化容器流服务。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="options">实时通道选项。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerStreamService(
        IDockerHostAccessor accessor,
        ILogger<ContainerStreamService> logger,
        IOptions<RealtimeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _accessor = accessor;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ContainerLogLine> StreamLogsAsync(
        string hostId,
        string containerId,
        ContainerLogStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(options);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = options.IncludeStderr,
            Timestamps = true,
            Follow = true,

            // 有 since → 从那一刻之后开始（服务端负责跳过已读过的部分）；
            // 无 since → 只要新产生的行（tail=0）。
            // ⚠️ 不能用 tail=N：那样会把历史也推一遍，与首屏历史重复。
            Tail = options.Since is null ? "0" : null,
            Since = options.Since?.ToString(CultureInfo.InvariantCulture),
        };

        var channel = Channel.CreateBounded<ContainerLogLine>(
            new BoundedChannelOptions(_options.LogChannelCapacity)
            {
                // 满了丢最旧的：推送端是 Docker 的读取回调，阻塞它等于停掉 Docker 侧的日志读取。
                // 被丢掉的行在下一批里表现为**序号缺口**，前端据此提示「已丢弃 N 行」。
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        var pump = new LogPump(options.StartSeq, channel.Writer, _options.LogBatchLines);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 后台读取 Docker 的日志流。**不 await**：这个 Task 在流结束时才完成，
        // 而流要等到容器停止才结束——await 它等于把枚举卡死。
        _ = PumpLogsAsync(client, containerId, parameters, pump, linked, completion);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally
        {
            // 枚举结束（消费方 break 或令牌取消）→ 停掉 Docker 侧那条流。
            // 不这样做会留下一条仍在读日志的连接。
            await linked.CancelAsync().ConfigureAwait(false);
        }

        // 流结束且带异常 → 翻译成业务错误抛给调用方。
        // 放在枚举之后而不是在 pump 里抛：pump 是后台任务，往那里抛只会变成「无人观察的异常」。
        var failure = completion.Task.IsFaulted ? completion.Task.Exception?.GetBaseException() : null;

        if (failure is not null)
        {
            throw DockerErrorTranslator.TranslateException(failure, $"容器 {containerId} 的日志流");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ContainerStatsSample> StreamStatsAsync(
        string hostId,
        string containerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ContainerStatsParameters
        {
            Stream = true,
            OneShot = false,
        };

        // 统计的容量很小：Docker 本身就 1 秒一帧，消费端（浏览器）也只显示最新值。
        // 队列只用来解耦「读取」与「推送」，不需要缓存历史。
        var channel = Channel.CreateBounded<ContainerStatsSample>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        var pump = new StatsPump(channel.Writer);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = PumpStatsAsync(client, containerId, parameters, pump, linked, completion);

        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return sample;
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
        }

        var failure = completion.Task.IsFaulted ? completion.Task.Exception?.GetBaseException() : null;

        if (failure is not null)
        {
            throw DockerErrorTranslator.TranslateException(failure, $"容器 {containerId} 的资源采样流");
        }
    }

    /// <summary>
    /// 后台读取 Docker 的日志流。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="parameters">读取参数。</param>
    /// <param name="pump">行收集器。</param>
    /// <param name="linked">与调用方令牌链接的取消源。</param>
    /// <param name="completion">完成信号（异常通过它传递）。</param>
    private static async Task PumpLogsAsync(
        IDockerClient client,
        string containerId,
        ContainerLogsParameters parameters,
        LogPump pump,
        CancellationTokenSource linked,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await client.Containers
                .GetContainerLogsAsync(containerId, parameters, pump, linked.Token)
                .ConfigureAwait(false);

            completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            // 订阅者断开：正常路径，不算故障。
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            pump.Complete();
        }
    }

    /// <summary>
    /// 后台读取 Docker 的资源采样流。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="parameters">读取参数。</param>
    /// <param name="pump">采样收集器。</param>
    /// <param name="linked">与调用方令牌链接的取消源。</param>
    /// <param name="completion">完成信号（异常通过它传递）。</param>
    private static async Task PumpStatsAsync(
        IDockerClient client,
        string containerId,
        ContainerStatsParameters parameters,
        StatsPump pump,
        CancellationTokenSource linked,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await client.Containers
                .GetContainerStatsAsync(containerId, parameters, pump, linked.Token)
                .ConfigureAwait(false);

            completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            // 订阅者断开：正常路径，不算故障。
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            pump.Complete();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ExportLogsAsync(
        string hostId,
        string containerId,
        long? since = null,
        bool includeStderr = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = includeStderr,

            // 不取时间戳：下载的是「原始日志」，时间戳是界面展示用的。
            Timestamps = false,

            // 不跟随、不限行数：读完即止，要的就是完整的那一份。
            Follow = false,
            Tail = null,
            Since = since?.ToString(CultureInfo.InvariantCulture),
        };

        var channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        var pump = new RawLinePump(channel.Writer);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = PumpExportAsync(client, containerId, parameters, pump, linked, completion);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
        }

        var failure = completion.Task.IsFaulted ? completion.Task.Exception?.GetBaseException() : null;

        if (failure is not null)
        {
            throw DockerErrorTranslator.TranslateException(failure, $"容器 {containerId} 的日志导出");
        }
    }

    /// <summary>
    /// 后台读取 Docker 的日志（导出用，不跟随）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="parameters">读取参数。</param>
    /// <param name="pump">行收集器。</param>
    /// <param name="linked">与调用方令牌链接的取消源。</param>
    /// <param name="completion">完成信号（异常通过它传递）。</param>
    private static async Task PumpExportAsync(
        IDockerClient client,
        string containerId,
        ContainerLogsParameters parameters,
        RawLinePump pump,
        CancellationTokenSource linked,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await client.Containers
                .GetContainerLogsAsync(containerId, parameters, pump, linked.Token)
                .ConfigureAwait(false);

            completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            pump.Complete();
        }
    }

    /// <summary>
    /// 日志行的同步收集器。
    /// </summary>
    /// <remarks>
    /// <para><b>必须是同步的</b>：<c>System.Progress&lt;T&gt;</c> 会把回调 Post 到线程池，
    /// 于是「Docker 流结束」与「最后几行被处理完」之间出现时间窗——日志会缺尾巴。
    /// SDK 在一条读取循环里顺序回调，因此这里不需要加锁。</para>
    /// </remarks>
    private sealed class LogPump : IProgress<string>
    {
        private readonly ChannelWriter<ContainerLogLine> _writer;
        private readonly ContainerLogParser _parser = new(stripTimestampPrefix: true);
        private int _seq;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="startSeq">起始行序号。</param>
        /// <param name="writer">频道写入端。</param>
        /// <param name="batchLines">批大小（仅用于日志；本收集器逐行写）。</param>
        public LogPump(int startSeq, ChannelWriter<ContainerLogLine> writer, int batchLines)
        {
            ArgumentNullException.ThrowIfNull(writer);

            _writer = writer;
            _seq = startSeq;
        }

        /// <summary>
        /// 结束频道（通知消费方流已结束）。
        /// </summary>
        public void Complete() => _writer.TryComplete();

        /// <inheritdoc />
        public void Report(string value)
        {
            foreach (var parsed in _parser.Append(value))
            {
                // TryWrite 在 DropOldest 模式下永远成功：满了就丢最旧的。
                // 被丢掉的行已经占用了序号 → 消费方看到的是**序号缺口**，
                // 因此不会把缺口误认为「没日志」。
                _writer.TryWrite(new ContainerLogLine(_seq++, parsed.Timestamp, parsed.Content));
            }
        }
    }

    /// <summary>
    /// 原始日志行的同步收集器（导出用）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LogPump"/> 的区别：<b>不解析时间戳</b>、<b>不编行号</b>，
    /// 只是把 SDK 回调的行原样转发出去。下载要的是「原始日志」。
    /// </remarks>
    private sealed class RawLinePump : IProgress<string>
    {
        private readonly ChannelWriter<string> _writer;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="writer">频道写入端。</param>
        public RawLinePump(ChannelWriter<string> writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            _writer = writer;
        }

        /// <summary>
        /// 结束频道（通知消费方流已结束）。
        /// </summary>
        public void Complete() => _writer.TryComplete();

        /// <inheritdoc />
        public void Report(string value) => _writer.TryWrite(value);
    }

    /// <summary>
    /// 资源采样的同步收集器。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么第一帧要丢掉</b>：CPU 百分比必须由<b>相邻两帧</b>相减得到
    /// （<c>cpu_stats</c> 与 <c>precpu_stats</c> 的差）。第一帧没有「前一帧」，
    /// 算出来的值要么是 0（看起来像「没在跑」）要么是天文数字。</para>
    /// <para><b>为什么在线核数要用容器的视角</b>：Docker 给的 <c>cpu_stats.cpu_usage.total_usage</c>
    /// 是<b>所有核</b>的累计值，而分母 <c>system_cpu_usage</c> 也是所有核。
    /// 因此公式里要乘以在线核数，得到的是「相对单核的百分比」——
    /// 这正是 <c>docker stats</c> 的口径（一个容器用满 4 核可以显示 400%）。</para>
    /// </remarks>
    private sealed class StatsPump : IProgress<ContainerStatsResponse>
    {
        private readonly ChannelWriter<ContainerStatsSample> _writer;
        private CPUStats? _previous;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="writer">频道写入端。</param>
        public StatsPump(ChannelWriter<ContainerStatsSample> writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            _writer = writer;
        }

        /// <summary>
        /// 结束频道（通知消费方流已结束）。
        /// </summary>
        public void Complete() => _writer.TryComplete();

        /// <inheritdoc />
        public void Report(ContainerStatsResponse value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var current = value.CPUStats;
            var previous = _previous;
            _previous = current;

            if (current?.CPUUsage is null)
            {
                return;
            }

            var sample = new ContainerStatsSample(
                value.ID ?? string.Empty,
                CalculateCpuPercent(current, previous),
                (long)(value.MemoryStats?.Usage ?? 0),
                (long)(value.MemoryStats?.Limit ?? 0),
                SumNetwork(value.Networks, static network => network.RxBytes),
                SumNetwork(value.Networks, static network => network.TxBytes),
                DateTimeOffset.UtcNow);

            _writer.TryWrite(sample);
        }

        /// <summary>
        /// 由相邻两帧计算 CPU 百分比。
        /// </summary>
        /// <param name="current">当前帧。</param>
        /// <param name="previous">前一帧；<see langword="null"/> 表示还没有前一帧。</param>
        /// <returns>CPU 百分比（0–100×核数）。</returns>
        private static double CalculateCpuPercent(CPUStats current, CPUStats? previous)
        {
            if (previous?.CPUUsage is null)
            {
                return 0;
            }

            var cpuDelta = current.CPUUsage.TotalUsage - previous.CPUUsage.TotalUsage;
            var systemDelta = (current.SystemUsage ?? 0) - (previous.SystemUsage ?? 0);

            if (systemDelta <= 0 || cpuDelta <= 0)
            {
                return 0;
            }

            // 在线核数：守护进程给的是「本容器可用的核数」；
            // 拿不到时退化成 1（此时百分比的口径是「单核占用」）。
            var onlineCpus = current.OnlineCPUs is { } cpus && cpus > 0 ? cpus : 1;

            return (double)cpuDelta / systemDelta * onlineCpus * 100;
        }

        /// <summary>
        /// 汇总各网卡的字节数。
        /// </summary>
        /// <param name="networks">各网卡的数据。</param>
        /// <param name="selector">取哪个方向的字节数。</param>
        /// <returns>求和结果。</returns>
        private static long SumNetwork(
            IDictionary<string, NetworkStats>? networks,
            Func<NetworkStats, ulong> selector)
        {
            if (networks is null || networks.Count == 0)
            {
                return 0;
            }

            long total = 0;

            foreach (var network in networks.Values)
            {
                total += (long)selector(network);
            }

            return total;
        }
    }
}
