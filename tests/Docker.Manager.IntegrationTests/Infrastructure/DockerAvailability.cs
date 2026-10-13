using System.Net.Sockets;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// 宿主 Docker 守护进程的可用性探测（供 <see cref="DockerFactAttribute"/> 判定是否跳过用例）。
/// </summary>
/// <remarks>
/// <para><b>为什么不引入 Testcontainers</b>：本项目的被测依赖<b>就是</b>宿主 Docker daemon 本身
/// （列容器、拉镜像等），把它装进一个容器既不能验证真实行为，又多一层依赖。
/// 见 <c>docs/constraints.md</c> 的「集成测试门槛」。</para>
/// <para><b>探测为什么既看文件也试连接</b>：socket 文件可能存在而守护进程已停——
/// 那种情况下"文件在"会让用例以为可以跑，然后以一个含义不明的失败告终。
/// 因此再尝试一次短超时的连接：连通才算可用。</para>
/// <para><b>结果只算一次</b>：探测发生在特性（attribute）构造期，xUnit 会为每个用例求值一次特性；
/// 用 <see cref="Lazy{T}"/> 缓存，避免每个用例都去连一次 socket。</para>
/// </remarks>
public static class DockerAvailability
{
    /// <summary>默认的宿主 socket 路径（Linux / macOS）。</summary>
    public const string DefaultSocketPath = "/var/run/docker.sock";

    /// <summary>探测连接的超时（毫秒）。</summary>
    /// <remarks>取值要小：它在测试发现阶段同步执行，卡住会拖慢整个测试会话。</remarks>
    public const int ProbeTimeoutMilliseconds = 500;

    private static readonly Lazy<bool> ProbeResult = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 宿主 Docker 守护进程是否可用。
    /// </summary>
    public static bool IsAvailable => ProbeResult.Value;

    /// <summary>
    /// 不可用时的跳过原因（可直接展示给测试报告）。
    /// </summary>
    public static string SkipReason =>
        $"宿主 Docker socket（{DefaultSocketPath}）不可达——需要真实 Docker 守护进程的用例已跳过。"
        + "在开发机上请确认 Docker 正在运行；在 CI 上这类用例本就允许跳过。";

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Windows 走命名管道，路径形态不同（0.0.8 未覆盖）。
            return false;
        }

        if (!File.Exists(DefaultSocketPath))
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            // 这里刻意同步等待：特性构造不可能是异步的。超时给得很短，且失败是常态而非异常。
            socket
                .ConnectAsync(new UnixDomainSocketEndPoint(DefaultSocketPath))
                .Wait(ProbeTimeoutMilliseconds);

            return socket.Connected;
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException)
        {
            return false;
        }
        catch (AggregateException exception) when (exception.InnerException is SocketException or IOException)
        {
            // Wait(TimeSpan) 会把异步异常包成 AggregateException。
            return false;
        }
    }
}
