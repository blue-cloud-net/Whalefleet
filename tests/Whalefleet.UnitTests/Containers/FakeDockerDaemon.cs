using System.Net;
using System.Text;
using Docker.DotNet;
using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Hosts;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 只回应 <c>GET /containers/json</c> 的假 Docker 守护进程（回环 HTTP）。
/// </summary>
/// <remarks>
/// <para><b>为什么起一个假的守护进程而不是 mock <c>IDockerClient</c></b>：
/// 手写 <c>IDockerClient</c> 的假实现要实现十几个子接口（容器 / 镜像 / 网络 / 卷 /
/// 插件 / Swarm…），二百多行样板代码；而 <see cref="HttpListener"/> 只要几十行就能把
/// <c>GET /containers/json</c> 响应喂回去，还顺带覆盖了
/// <b>Docker.DotNet 的 HTTP 往返 + JSON 反序列化</b>——那恰恰是列表接口 P95 里
/// 与真实使用最接近的部分。mock 掉它等于把最贵的一段从测量里抹掉。</para>
///
/// <para><b>刻意不校验路径</b>：这些测试要的是「稳定的、可重复的响应」，
/// 多一层路由判断只会让测试变脆。所有 <c>/</c> 前缀的请求都返回同一份列表——
/// Docker.DotNet 会带 <c>/v1.42/containers/json</c> 这样的版本前缀。</para>
///
/// <para><b>响应体可以是随调用变化的</b>（<see cref="CallCount"/> + 工厂构造）：
/// 「端口顺序每次可能不同」这个 bug 正是要靠「同一个容器、两次请求、返回顺序不同」
/// 才能钉住——只给一份固定顺序的响应，测不出排序是否生效。</para>
/// </remarks>
internal sealed class FakeDockerDaemon : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<int, string> _bodyFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _callCount;

    /// <summary>用固定响应体构造。</summary>
    /// <param name="body">每次请求都返回这一份 JSON。</param>
    public FakeDockerDaemon(string body)
        : this(_ => body)
    {
    }

    /// <summary>用「随调用次数变化」的响应体工厂构造。</summary>
    /// <param name="bodyFactory">入参是已完成的调用次数（从 0 开始），返回该次要返回的 JSON。</param>
    public FakeDockerDaemon(Func<int, string> bodyFactory)
    {
        ArgumentNullException.ThrowIfNull(bodyFactory);

        _bodyFactory = bodyFactory;

        // 端口 0 = 让系统分配空闲端口，避免并行测试互相抢端口
        var port = FreeTcpPort();
        Endpoint = new Uri($"tcp://127.0.0.1:{port}");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _loop = Task.Run(ServeAsync);
    }

    /// <summary>守护进程端点（交给 Docker.DotNet 客户端）。</summary>
    public Uri Endpoint { get; }

    /// <summary>已处理的请求数。</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>取一个空闲的 TCP 端口。</summary>
    /// <returns>端口号。</returns>
    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    /// <summary>回环 HTTP 服务循环。</summary>
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

            var body = _bodyFactory(Interlocked.Increment(ref _callCount) - 1);
            var payload = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    /// <inheritdoc />
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
/// 列表路径只会用到 <c>GetClientAsync</c>；其余成员显式抛异常而不是
/// 悄悄返回默认值——若将来这条路径改用了别的成员，测试会立刻炸在这里，
/// 而不是静默退化成一个「什么都没测」的绿测。
/// </remarks>
internal sealed class StubDockerHostAccessor(Uri endpoint) : IDockerHostAccessor
{
    /// <summary>
    /// 取一个指向假守护进程的客户端。
    /// </summary>
    /// <param name="hostId">主机标识（忽略）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Docker 客户端。</returns>
    public async Task<IDockerClient> GetClientAsync(
        string hostId,
        CancellationToken cancellationToken = default)
    {
        // 包一层 async：Build() 返回具体类型 DockerClient，直接 return 会触发
        // Task<DockerClient> → Task<IDockerClient> 的协变转换报错
        await Task.CompletedTask.ConfigureAwait(false);

        return new DockerClientBuilder().WithEndpoint(endpoint).Build();
    }

    /// <inheritdoc />
    public Task<DockerHost> ResolveAsync(
        string hostId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("测试不走主机解析路径");

    /// <inheritdoc />
    public Task<bool> TestConnectionAsync(
        DockerHostConnection connection,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public void Invalidate(DockerHostConnection connection)
        => throw new NotSupportedException();
}
