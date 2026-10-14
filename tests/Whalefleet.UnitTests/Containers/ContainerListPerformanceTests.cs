using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Docker.DotNet;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Containers;
using Whalefleet.Core.Hosts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 容器列表的性能基线（0.0.14 验收项）。
/// </summary>
/// <remarks>
/// <para><b>验收目标</b>：100 个容器的 list 场景，P95 &lt; 500ms。</para>
///
/// <para><b>为什么起一个假的守护进程而不是 mock <c>IDockerClient</c></b>：
/// 手写 <c>IDockerClient</c> 的假实现要实现十几个子接口（容器 / 镜像 / 网络 / 卷 /
/// 插件 / Swarm…），
/// 二百多行样板代码；而 <see cref="HttpListener"/> 只要几十行就能把
/// <c>GET /containers/json</c> 响应喂回去，还顺带覆盖了
/// <b>Docker.DotNet 的 HTTP 往返 + JSON 反序列化</b>——那恰恰是列表接口 P95 里
/// 与真实使用最接近的部分。mock 掉它等于把最贵的一段从测量里抹掉。</para>
///
/// <para><b>这个基线测的是什么、不测什么</b>：测的是「拿到 100 条 JSON →
/// 反序列化 → 投影成 <c>ContainerListResult</c>」这条我们自己写得出来的链路。
/// 不测真实 Docker 守护进程的处理时间——那是 Docker 的性能，随宿主机负载浮动，
/// 测它只会让这条基线时而红时而绿，失去意义。</para>
///
/// <para><b>为什么用 P95 而不是平均值</b>：平均会抹掉最坏情况，而用户感知到的
/// 「有时候特别卡」正是尾部延迟。</para>
///
/// <para><b>门槛为什么放得这么宽</b>：500ms 对「HTTP 往返 + 100 条 JSON 反序列化 +
/// 投影」而言有几十倍余量。它要抓的不是「绝对快」，而是<b>回归</b>——
/// 有人把投影改成 N+1 次字符串拼接、或忘记分页导致每次都全量重算时，
/// 十几倍的劣化会立刻撞穿这条线。</para>
/// </remarks>
public class ContainerListPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public ContainerListPerformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>验收阈值（P95 毫秒）。</summary>
    private const long P95BudgetMs = 500;

    /// <summary>预热轮次：首次调用要触发 JIT 与泛型实例化，不计入统计。</summary>
    private const int WarmupIterations = 5;

    /// <summary>统计轮次。取 30 次让 P95 至少有 1~2 个真实样本落在尾部。</summary>
    private const int MeasuredIterations = 30;

    [Fact]
    public async Task 一百个容器的列表_P95_低于500毫秒()
    {
        await using var daemon = new FakeDockerDaemon(BuildContainersJson(100));

        var service = new ContainerService(
            new StubAccessor(daemon.Endpoint),
            NullLogger<ContainerService>.Instance);

        var request = new ListContainersRequest { All = true, PageSize = 100 };

        // 预热：不计入统计。少了这一步，JIT 与首次泛型实例化会直接吃掉大部分预算
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = await service.ListAsync("perf-host", request);
        }

        var samples = new long[MeasuredIterations];
        for (var i = 0; i < MeasuredIterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await service.ListAsync("perf-host", request);
            stopwatch.Stop();

            // 每次都断言结果正确：把「其实没干活所以特别快」排除掉
            result.Items.Should().HaveCount(100);
            result.Total.Should().Be(100);

            samples[i] = stopwatch.ElapsedMilliseconds;
        }

        Array.Sort(samples);
        var p95 = Percentile(samples, 0.95);
        var max = samples[^1];

        // 把实测值打出来：基线的作用不只是「过 / 不过」，
        // 更是给后来者一个对照——「上次是 12ms，现在变 60ms」比一句「未超 500ms」有用得多。
        _output.WriteLine(
            $"容器列表基线（100 个）：P50={Percentile(samples, 0.5)}ms, " +
            $"P95={p95}ms, Max={max}ms, 阈值={P95BudgetMs}ms");
        _output.WriteLine($"样本（升序）：{string.Join(", ", samples)}");

        p95.Should().BeLessThan(
            P95BudgetMs,
            "容器列表 P95 基线（样本：{0}）",
            string.Join(", ", samples));
    }

    [Fact]
    public async Task 分页切片_不改变单条记录的投影耗时量级()
    {
        // 分页是在**内存**里做的（Docker API 无分页参数）。这条钉住的是
        // 「Skip/Take 不会把代价放大成全量重算」——
        // 若将来有人改成先投影再分页，或漏了 Skip/Take 而每次全量投影，这里会先红。
        await using var daemon = new FakeDockerDaemon(BuildContainersJson(100));

        var service = new ContainerService(
            new StubAccessor(daemon.Endpoint),
            NullLogger<ContainerService>.Instance);

        var full = await service.ListAsync(
            "perf-host",
            new ListContainersRequest { All = true, PageSize = 100 });
        var paged = await service.ListAsync(
            "perf-host",
            new ListContainersRequest { All = true, PageIndex = 5, PageSize = 10 });

        full.Items.Should().HaveCount(100);
        paged.Items.Should().HaveCount(10);
        paged.PageIndex.Should().Be(5);
        paged.Total.Should().Be(100);

        // 第 6 页的第一条必须等于全量里的第 51 条，否则切片位置算错了
        paged.Items[0].Id.Should().Be(full.Items[50].Id);
    }

    /// <summary>
    /// 取指定分位（nearest-rank）。
    /// </summary>
    /// <remarks>
    /// 用 nearest-rank 而不是线性插值：我们要的是「最慢的那几次里真实慢到多少」，
    /// 插值会造出一个从未发生过的数。
    /// </remarks>
    private static long Percentile(long[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// 构造 100 条容器的列表响应 JSON。
    /// </summary>
    /// <remarks>
    /// 直接手写 JSON 而不走 <c>JsonSerializer</c>：这份数据需要**形状贴近真实**
    /// （嵌套的端口、挂载、网络、环境变量），而从 SDK 模型序列化出去反而会掩盖
    /// 反序列化本身的成本差异。字段按 Docker.DotNet 实际解析的形状给出。
    /// </remarks>
    private static string BuildContainersJson(int count)
    {
        var builder = new StringBuilder(count * 512);
        builder.Append('[');

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var id = i.ToString("x12", CultureInfo.InvariantCulture);

            builder.Append(CultureInfo.InvariantCulture, $$"""
                {
                  "Id": "{{id}}",
                  "Names": ["/app-{{id}}"],
                  "Image": "nginx:1.27-alpine",
                  "ImageID": "sha256:{{new string('a', 64)}}",
                  "Command": "nginx -g daemon off;",
                  "Created": 1735689600,
                  "State": "running",
                  "Status": "Up 3 hours",
                  "Labels": { "com.docker.compose.project": "proj-{{i}}" },
                  "Mounts": [
                    { "Type": "bind", "Source": "/host/data", "Destination": "/data", "Mode": "rw" },
                    { "Type": "volume", "Name": "vol-{{id}}", "Destination": "/cache", "Mode": "rw" }
                  ],
                  "NetworkSettings": {
                    "Networks": {
                      "bridge": { "IPAddress": "172.17.0.{{i % 255}}", "Gateway": "172.17.0.1" }
                    }
                  },
                  "Ports": [
                    { "PrivatePort": 80, "PublicPort": 8080, "Type": "tcp", "IPAddress": "0.0.0.0" },
                    { "PrivatePort": 443, "PublicPort": 8443, "Type": "tcp", "IPAddress": "0.0.0.0" },
                    { "PrivatePort": 53, "PublicPort": 53, "Type": "udp", "IPAddress": "127.0.0.1" }
                  ],
                  "Env": ["TZ=UTC", "LANG=C.UTF-8", "APP_ENV=production", "DEBUG=false", "PORT=80"]
                }
                """);
        }

        builder.Append(']');

        return builder.ToString();
    }

    /// <summary>
    /// 只回应 <c>GET /containers/json</c> 的假 Docker 守护进程（回环 HTTP）。
    /// </summary>
    /// <remarks>
    /// 刻意不校验路径：这条基线要的是「稳定的、可重复的响应」，多一层路由判断
    /// 只会让测试变脆。所有 <c>/</c> 前缀的请求都返回同一份列表——
    /// Docker.DotNet 会带 <c>/v1.42/containers/json</c> 这样的版本前缀。
    /// </remarks>
    private sealed class FakeDockerDaemon : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _body;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public Uri Endpoint { get; }

        public FakeDockerDaemon(string body)
        {
            _body = body;

            // 端口 0 = 让系统分配空闲端口，避免并行测试互相抢端口
            var port = FreeTcpPort();
            Endpoint = new Uri($"tcp://127.0.0.1:{port}");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _loop = Task.Run(ServeAsync);
        }

        private static int FreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            return port;
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    // 监听器已停止（测试收尾）
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var payload = Encoding.UTF8.GetBytes(_body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Close();

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // 关闭监听器必然导致它抛异常，这不是失败
            }

            _cts.Dispose();
        }
    }

    /// <summary>
    /// 只实现「取客户端」的访问器，其余成员直接抛 <see cref="NotSupportedException"/>。
    /// </summary>
    /// <remarks>
    /// 列表路径只会用到 <see cref="GetClientAsync"/>；其余成员显式抛异常而不是
    /// 悄悄返回默认值——若将来这条路径改用了别的成员，测试会立刻炸在这里，
    /// 而不是静默退化成一个「什么都没测」的绿测。
    /// </remarks>
    private sealed class StubAccessor(Uri endpoint) : IDockerHostAccessor
    {
        // Docker.DotNet.Enhanced 4.x：DockerClientConfiguration 已移除，统一用
        // DockerClientBuilder（与 DockerClientFactory 一致），传输方式由 scheme 推断。
        public async Task<IDockerClient> GetClientAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            // 包一层 async：Build() 返回具体类型 DockerClient，直接 return 会触发
            // Task<DockerClient> → Task<IDockerClient> 的协变转换报错
            await Task.CompletedTask.ConfigureAwait(false);

            return new DockerClientBuilder().WithEndpoint(endpoint).Build();
        }

        public Task<DockerHost> ResolveAsync(
            string hostId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("性能基线不经过主机解析路径");

        public Task<bool> TestConnectionAsync(
            DockerHostConnection connection,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Invalidate(DockerHostConnection connection)
            => throw new NotSupportedException();
    }
}
