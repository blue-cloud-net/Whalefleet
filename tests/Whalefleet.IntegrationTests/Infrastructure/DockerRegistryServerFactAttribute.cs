using System.Net.Sockets;
using Xunit;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// 「需要本机跑着一个可推送的私有 Registry」的用例标注。
/// </summary>
/// <remarks>
/// <para><b>为什么要单独一个特性</b>：推镜像的<b>成功路径</b>必须有一个真实的 Registry 才跑得起来。
/// 而成功路径恰恰是 0.4.0 的验收标准（「配置一个私有 Registry 后能 push 成功」）——
/// 不能因为「大多数开发机上没有现成的私有 Registry」就让这条验收标准形同虚设。</para>
/// <para><b>宿主 Registry 的约定地址</b>：<c>127.0.0.1:5000</c>。这是 <c>registry:2</c>
/// 的默认监听端口，也是 Compose 文件与绝大多数教程用的地址。
/// 端口集中定义在 <see cref="DockerAvailability"/>，免得散落在多个测试文件里各写一遍而漂移。</para>
/// <para><b>无认证也能推</b>：<c>registry:2</c> 默认接受<b>匿名写入</b>，
/// 因此本组验证的是「匿名推送 + 打标签 + 产物落在指定仓库」这条路径。</para>
/// </remarks>
public sealed class DockerRegistryServerFactAttribute : FactAttribute
{
    /// <summary>
    /// 初始化特性，并在宿主守护进程不可用或本机 Registry 不可达时给出跳过原因。
    /// </summary>
    public DockerRegistryServerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            this.Skip = DockerAvailability.SkipReason;
        }
        else if (!LocalRegistryServer.IsRunning)
        {
            this.Skip = DockerAvailability.LocalRegistrySkipReason;
        }
    }
}

/// <summary>
/// 宿主本地私有 Registry 的探测。
/// </summary>
public static class LocalRegistryServer
{
    private static readonly Lazy<bool> ProbeResult =
        new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>探测连接的超时（毫秒）。</summary>
    private const int ProbeTimeoutMilliseconds = 500;

    /// <summary>
    /// 本机 Registry 是否可连。
    /// </summary>
    /// <remarks>
    /// 只判「TCP 能不能连上」：能连上就意味着有人在那儿跑着 Registry，
    /// 而它是不是真的接受推送，交给用例本身去验证（推失败会拿到 1807）。
    /// 这里刻意不去做 <c>GET /v2/</c> ——那会引入一次真实 HTTP 往返，
    /// 让「探测」本身有了依赖与超时两种失败模式。
    /// </remarks>
    public static bool IsRunning => ProbeResult.Value;

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();

            return client
                .ConnectAsync(DockerAvailability.DefaultRegistryHost, DockerAvailability.DefaultRegistryPort)
                .Wait(ProbeTimeoutMilliseconds);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (AggregateException)
        {
            // ConnectAsync(...).Wait 在失败时抛 AggregateException 包着 SocketException
            return false;
        }
    }
}
