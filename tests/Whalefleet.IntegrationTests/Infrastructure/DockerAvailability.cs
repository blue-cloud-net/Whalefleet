using System.Net.Sockets;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Whalefleet.IntegrationTests.Infrastructure;

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

    /// <summary>Docker Hub 清单服务的 HTTPS 端口。</summary>
    private const int RegistryPort = 443;
    private static readonly Lazy<bool> ProbeResult = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string?> AvailableImage =
        new(DiscoverImage, LazyThreadSafetyMode.ExecutionAndPublication);

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

    /// <summary>
    /// 无镜像时的跳过原因。
    /// </summary>
    public static string ImageSkipReason =>
        "宿主守护进程上没有已打标签的镜像——需要镜像来建容器的用例已跳过（刻意不联网拉取："
        + "那会让用例依赖网络与镜像仓库可用性，而它们与本次验证的目标无关）。";

    /// <summary>
    /// 镜像仓库不可达时的跳过原因。
    /// </summary>
    public static string RegistrySkipReason =>
        "连不上镜像仓库（daemon.json 里配置的加速器与 Docker Hub 都试过了）——需要拉取镜像的用例已跳过。"
        + "这类用例验证的是「拉取成功路径」，离线时它无话可说；拉取的**失败**路径由不需要网络的用例覆盖。";

    /// <summary>
    /// Docker Hub 的清单服务主机（未配加速器时拉取公网镜像必经的入口）。
    /// </summary>
    public const string RegistryHost = "registry-1.docker.io";

    /// <summary>
    /// 镜像仓库是否可达（能建立到某个镜像仓库的 TCP 连接）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么单独探测而不是靠用例跑挂了再判断</b>：「拉取失败」是本组用例要验证的正常结果，
    /// 它和「网络不通导致失败」在响应上长得一模一样。把网络状况单独探测出来，
    /// 「没网」才会表现为 Skip（可忽略）而不是 Fail（要追）。</para>
    /// <para><b>为什么先试 daemon.json 里的加速器</b>：国内环境普遍配了 <c>registry-mirrors</c>，
    /// 守护进程只走加速器、不直连 Docker Hub。若只探 Docker Hub，在这类机器上会永远判定
    /// 「不可达」，把本来能跑的成功路径用例全部跳过——比不探测更糟。</para>
    /// </remarks>
    public static bool IsRegistryReachable => RegistryProbe.Value;

    private static readonly Lazy<bool> RegistryProbe =
        new(ProbeRegistry, LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool ProbeRegistry()
    {
        foreach (var host in RegistryHosts())
        {
            if (CanConnect(host))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 候选镜像仓库主机：daemon.json 里配置的加速器优先，其后是 Docker Hub。
    /// </summary>
    /// <returns>主机名列表（可能为空——读不到配置时只有 Docker Hub）。</returns>
    private static IEnumerable<string> RegistryHosts()
    {
        foreach (var mirror in ReadConfiguredMirrors())
        {
            yield return mirror;
        }

        yield return RegistryHost;
    }

    /// <summary>
    /// 读 <c>/etc/docker/daemon.json</c> 里配置的镜像加速器。
    /// </summary>
    /// <returns>加速器主机名；读不到或格式不认识时为空。</returns>
    /// <remarks>
    /// 只做尽力而为的读取：文件不存在、权限不足、JSON 格式变了，都直接返回空
    /// （后面的 Docker Hub 探测会接手）。因为这只是一条测试的跳过判定，
    /// 不值得为了它引入配置解析依赖或让整个测试会话失败。
    /// </remarks>
    private static IEnumerable<string> ReadConfiguredMirrors()
    {
        const string path = "/etc/docker/daemon.json";

        if (!File.Exists(path))
        {
            yield break;
        }

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            root = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            yield break;
        }

        if (!root.TryGetProperty("registry-mirrors", out var mirrors)
            || mirrors.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var mirror in mirrors.EnumerateArray())
        {
            if (mirror.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(mirror.GetString(), UriKind.Absolute, out var uri))
            {
                continue;
            }

            // 加速器常写成 https://mirror.example.com/ 或裸主机名，两种都要能处理。
            if (!string.IsNullOrWhiteSpace(uri.Host))
            {
                yield return uri.Host;
            }
        }
    }

    /// <summary>
    /// 尝试建立到指定主机的 TCP 连接。
    /// </summary>
    /// <param name="host">主机名。</param>
    /// <returns>连得上返回 <see langword="true"/>。</returns>
    private static bool CanConnect(string host)
    {
        try
        {
            using var client = new TcpClient();

            return client
                .ConnectAsync(host, RegistryPort)
                .Wait(ProbeTimeoutMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// 宿主上第一张「有名字的」镜像（<c>repo:tag</c>）；无可用镜像时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <b>为什么在本地镜像里挑而不是写死 <c>alpine:latest</c></b>：写死镜像名意味着用例的通过与否
    /// 取决于「那台机器恰好拉过哪张镜像」，在干净环境里必然红。挑一张现成镜像则让用例只依赖
    /// 「有镜像」这个真正必要的前提，且不会引入网络依赖。
    /// </remarks>
    public static string? ImageReference => AvailableImage.Value;

    private static string? DiscoverImage()
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            using var client = new DockerClientBuilder()
                .WithEndpoint(new Uri($"unix://{DefaultSocketPath}"))
                .Build();

            // 与探测同理：特性构造不可能是异步的，这里只能同步等待。
            var images = client.Images
                .ListImagesAsync(new ImagesListParameters { All = false })
                .GetAwaiter()
                .GetResult();

            foreach (var image in images)
            {
                var tag = image.RepoTags?.FirstOrDefault(
                    candidate => !String.IsNullOrWhiteSpace(candidate)
                        && !candidate.Contains("<none>", StringComparison.Ordinal));

                if (tag is not null)
                {
                    return tag;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 发现阶段任何失败都只意味着「不能用真实镜像跑用例」，不应把异常抛到测试发现里。
            return null;
        }
    }

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
