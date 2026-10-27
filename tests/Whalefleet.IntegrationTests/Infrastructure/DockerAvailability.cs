using System.Globalization;
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

    /// <summary>探测用容器的存活时长（秒）。</summary>
    private const int ProbeContainerSeconds = 30;

    /// <summary>启动后等多久再查状态（毫秒）。</summary>
    /// <remarks>
    /// 必须给容器一点时间「秒退」：入口点吃掉命令的容器是「启动成功后立刻退出」的，
    /// 立即查状态可能赶在退出之前而误判为可用。
    /// </remarks>
    private const int ProbeSettleMilliseconds = 1000;
    private static readonly Lazy<bool> ProbeResult = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string?> AvailableImage =
        new(DiscoverKeepAliveCapableImage, LazyThreadSafetyMode.ExecutionAndPublication);

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
    /// 宿主本地私有仓库 的主机（<c>registry:2</c> 的默认监听地址）。
    /// </summary>
    public const string DefaultRegistryHost = "127.0.0.1";

    /// <summary>
    /// 宿主本地私有仓库 的端口（<c>registry:2</c> 的默认监听端口）。
    /// </summary>
    public const int DefaultRegistryPort = 5000;

    /// <summary>
    /// 宿主本地私有仓库 的完整地址。
    /// </summary>
    public const string DefaultRegistryEndpoint = "127.0.0.1:5000";

    /// <summary>
    /// 本机没有跑着私有仓库 时的跳过原因。
    /// </summary>
    public const string LocalRegistrySkipReason =
        "本机没有跑着可推送的私有仓库——推镜像成功路径的用例已跳过。"
        + "起一个即可：docker run -d --rm -p 5000:5000 --name whalefleet-test-registry registry:2";

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
    public static bool IsRegistryReachable => RepositoryProbe.Value;

    private static readonly Lazy<bool> RepositoryProbe =
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
    /// 宿主上「能承载一个<b>常驻</b>容器」的第一张有标签镜像；没有则 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不是「第一张有标签的镜像」</b>：镜像的 <c>ENTRYPOINT</c> 会<b>抢在</b>命令之前执行。
    /// 入口点是 <c>/app/server</c> 时，<c>cmd=["sleep","60"]</c> 实际跑的是
    /// <c>/app/server sleep 60</c>——服务进程把这个参数当成自己的参数，秒退。</para>
    /// <para>症状极具迷惑性：<b>创建成功、启动也成功</b>，但容器一查就是 <c>exited</c>。
    /// 而失败的是「容器应该保持运行」这种语义断言（暂停会报「未在运行」、网络接入查不到容器）——
    /// 看起来像业务代码坏了，实际是宿主上多了一张带奇怪入口点的镜像
    /// （<c>docker images</c> 的顺序随构建时间变，因此这是<b>随机</b>发生的）。</para>
    /// <para><b>探测方式</b>：真起一个 <c>sleep</c> 容器看它是否<b>还在跑</b>——
    /// 只有「起得来且留得住」这种问题才问得准（看 <c>Config.Entrypoint</c> 猜不出入口点会不会吃掉命令）。</para>
    /// <para><b>为什么在本地镜像里挑而不是写死 <c>alpine:latest</c></b>：写死镜像名意味着用例的通过与否
    /// 取决于「那台机器恰好拉过哪张镜像」，在干净环境里必然红。挑一张现成镜像则让用例只依赖
    /// 「有镜像」这个真正必要的前提，且不会引入网络依赖。</para>
    /// </remarks>
    public static string? ImageReference => AvailableImage.Value;

    /// <summary>
    /// 宿主上「能起一个 <c>/bin/sh</c> 容器」的第一张有标签镜像；没有则 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它</b>：有些用例要求容器<b>恰好输出确定的内容</b>（例如验证日志按行返回）。
    /// 而宿主上「第一张有标签的镜像」是任意的：<c>alpine</c> 没有入口点，跑 <c>sleep</c> 时<b>一行都不输出</b>；
    /// 本项目自己的镜像（<c>whalefleet:local</c>）入口点是 API 宿主，会输出<b>上百行</b>启动日志。
    /// 用例的断言不可能同时扛住这两种极端。</para>
    /// <para>因此这里给出的是「<b>能显式覆盖入口点</b>」的镜像：调用方把入口点写成
    /// <c>/bin/sh -c '…'</c>，输出就由用例自己说了算，不再受镜像默认入口点影响。</para>
    /// <para><b>探测方式</b>：真起一个 <c>/bin/sh -c 'exit 0'</c> 的容器看它是否正常退出——
    /// 只有「有没有 shell」这种问题才问得准（<c>Config.Entrypoint</c> 里看不出镜像里有没有 shell）。</para>
    /// </remarks>
    public static string? ShellImageReference => ShellCapableImage.Value;

    /// <summary>「能跑 shell 的镜像」的跳过原因。</summary>
    public const string ShellImageSkipReason =
        "宿主上没有能跑 /bin/sh 的镜像（可能是 scratch 类镜像）——"
        + "需要显式覆盖入口点的用例已跳过。";

    private static readonly Lazy<string?> ShellCapableImage =
        new(DiscoverShellCapableImage, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? DiscoverShellCapableImage()
    {
        foreach (var image in CandidateImages())
        {
            if (CanRunShell(image))
            {
                return image;
            }
        }

        return null;
    }

    /// <summary>
    /// 找出第一张能承载常驻容器的镜像。
    /// </summary>
    /// <returns>能承载常驻容器的镜像；没有则 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么逐张试而不是只看第一张</b>：宿主上的镜像列表随构建时间变化，
    /// 「第一张」随时可能变成一张入口点会吃掉命令的镜像。
    /// 逐张试到有一张能用为止，测试就不再受「那台机器上恰好有什么镜像」影响。
    /// </remarks>
    private static string? DiscoverKeepAliveCapableImage()
    {
        foreach (var image in CandidateImages())
        {
            if (CanKeepAlive(image))
            {
                return image;
            }
        }

        return null;
    }

    /// <summary>
    /// 起一个 <c>sleep 30</c> 的容器，看它是否<b>还活着</b>。
    /// </summary>
    /// <param name="image">候选镜像。</param>
    /// <returns>容器能持续运行返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>为什么不能只看启动结果</b>：启动接口对「入口点会秒退」的容器同样返回成功——
    /// 容器是「成功启动了然后立刻退出」的。必须等一会儿再查状态。
    /// </remarks>
    private static bool CanKeepAlive(string image)
    {
        DockerClient? client = null;
        string? containerId = null;

        try
        {
            client = new DockerClientBuilder()
                .WithEndpoint(new Uri($"unix://{DefaultSocketPath}"))
                .Build();

            var created = client.Containers
                .CreateContainerAsync(new CreateContainerParameters
                {
                    Image = image,
                    // ⚠️ **不覆盖入口点**：与用例的建容器方式完全一致。
                    // 覆盖了量到的就是「这张镜像有没有 shell」——那是另一个问题
                    // （由 ShellImageReference 回答）。
                    Cmd = ["sleep", ProbeContainerSeconds.ToString(CultureInfo.InvariantCulture)],
                })
                .GetAwaiter()
                .GetResult();

            containerId = created.ID;

            client.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters())
                .GetAwaiter()
                .GetResult();

            // 给它一点时间「秒退」；然后再看它是不是还在跑。
            Thread.Sleep(1000);

            var inspected = client.Containers
                .InspectContainerAsync(containerId)
                .GetAwaiter()
                .GetResult();

            return inspected.State?.Running == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 探测期的任何失败都只意味着「这台机器上这张镜像不能用来跑常驻容器」。
            return false;
        }
        finally
        {
            if (client is not null && containerId is not null)
            {
                try
                {
                    client.Containers
                        .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true })
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // 清理失败不影响判定结果（容器会在测试结束后被清掉）。
                }
            }

            client?.Dispose();
        }
    }

    /// <summary>
    /// 起一个 <c>/bin/sh -c 'exit 0'</c> 的容器，看它能不能正常跑起来。
    /// </summary>
    /// <param name="image">候选镜像。</param>
    /// <returns>跑得起来返回 <see langword="true"/>。</returns>
    private static bool CanRunShell(string image)
    {
        DockerClient? client = null;
        string? containerId = null;

        try
        {
            client = new DockerClientBuilder()
                .WithEndpoint(new Uri($"unix://{DefaultSocketPath}"))
                .Build();

            var created = client.Containers
                .CreateContainerAsync(new CreateContainerParameters
                {
                    Image = image,
                    Entrypoint = ["/bin/sh", "-c", "exit 0"],
                })
                .GetAwaiter()
                .GetResult();

            containerId = created.ID;

            client.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters())
                .GetAwaiter()
                .GetResult();

            var exited = client.Containers
                .WaitContainerAsync(containerId)
                .GetAwaiter()
                .GetResult();

            return exited.StatusCode == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 探测期的任何失败都只意味着「这台机器上这张镜像不能用来跑 shell」。
            return false;
        }
        finally
        {
            if (client is not null && containerId is not null)
            {
                try
                {
                    client.Containers
                        .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true })
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // 探针容器删不掉不该让发现阶段失败。
                }
            }

            client?.Dispose();
        }
    }

    private static string? DiscoverImage()
    {
        var candidates = CandidateImages();

        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>
    /// 宿主上所有带标签的镜像（按发现顺序）。
    /// </summary>
    /// <returns>镜像名列表；守护进程不可用或拉取失败时为空。</returns>
    /// <remarks>
    /// <b>为什么返回列表而不是一张</b>：「第一张镜像能用」是**假设**，
    /// 而宿主的镜像集合随时在变（构建一张新镜像就会改变顺序）。
    /// 把候选交给调用方逐张试，才能让测试只依赖「有一张能用」这个真正必要的前提。
    /// </remarks>
    private static List<string> CandidateImages()
    {
        if (!IsAvailable)
        {
            return [];
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

            var candidates = new List<string>();

            foreach (var image in images)
            {
                var tag = image.RepoTags?.FirstOrDefault(
                    candidate => !String.IsNullOrWhiteSpace(candidate)
                        && !candidate.Contains("<none>", StringComparison.Ordinal));

                if (tag is not null)
                {
                    candidates.Add(tag);
                }
            }

            return candidates;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 发现阶段任何失败都只意味着「不能用真实镜像跑用例」，不应把异常抛到测试发现里。
            return [];
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
