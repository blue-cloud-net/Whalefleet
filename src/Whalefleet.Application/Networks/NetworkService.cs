using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Networks;

/// <summary>
/// 网络用例（列表 / 详情 / 创建 / 删除），对应 <c>/api/v1/hosts/{hostId}/networks/*</c>。
/// </summary>
/// <remarks>
/// <para><b>主机校验不在这里</b>：主机是否存在（<c>1300</c>）与可见性（<c>1501</c>）由
/// Api 层的 <c>CurrentHostMiddleware</c> 在进入本服务前完成。</para>
/// <para><b>外部响应按可空处理</b>：SDK 把字段声明为非空，但值来自另一个进程的 JSON。
/// 自定义驱动（<c>macvlan</c> / <c>ipvlan</c> 等）常常不填 <c>IPAM.Config</c>，
/// 照着「一定有值」写会在渲染侧炸成 <c>9999</c>/500，而那只是「对端没给这个字段」。</para>
/// </remarks>
public sealed class NetworkService
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<NetworkService> _logger;

    /// <summary>
    /// 初始化网络用例。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public NetworkService(IDockerHostAccessor accessor, ILogger<NetworkService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// 列出主机上的网络。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部网络。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）、已停用（<c>1302</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>顺序沿用守护进程的返回顺序</b>：它按名字升序返回，是稳定的，
    /// 额外重排只会让「翻页时看到的东西变了」更难解释。
    /// 排序能力统一归 <c>0.16.0</c>（四类资源一次做齐）。
    /// </remarks>
    public async Task<NetworkListResult> ListAsync(
        string hostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var networks = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.ListNetworksAsync(cancellationToken: cancellationToken),
            "网络列表",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("主机 {HostId} 网络列表：共 {Count} 个", hostId, networks.Count);

        return new NetworkListResult([.. networks.Select(MapSummary)]);
    }

    /// <summary>
    /// 查询网络详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（网络 ID 或名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>网络详情投影。</returns>
    /// <exception cref="BusinessException">
    /// 网络标识为空或非法（<c>1000</c>）、网络不存在（<c>1401</c>）、主机不可达（<c>1301</c>）
    /// 或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<NetworkDetail> InspectAsync(
        string hostId,
        InspectNetworkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var networkId = DockerResourceName.Require(request.NetworkId, "networkId", "网络");
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var network = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.InspectNetworkAsync(networkId, cancellationToken),
            $"网络 {networkId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("主机 {HostId} 查询网络 {NetworkId} 详情", hostId, networkId);

        return MapDetail(network);
    }

    /// <summary>
    /// 创建网络。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">创建请求（名称、驱动、子网、网关、可否单独接入、标签）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建出的网络列表项（创建后立刻 inspect 一次得到）。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）、同名网络已存在或子网与现有网络重叠（<c>1404</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>创建后多发一次 inspect</b>：守护进程的创建响应只有 <c>Id</c> 与
    /// <c>Warning</c>，其余字段（驱动、IPAM、是否 internal）都得再读一次才拿得到。
    /// 与镜像 <c>pull</c> 同样的取舍——既然这次读取躲不掉，就顺手把完整对象一起回带，
    /// 省掉界面紧随其后的第二个请求，也让它不必为「刚建的这一条」走特例。</para>
    /// <para><b>Docker 用 <c>403</c> 表达两类输入冲突</b>：「同名网络已存在」与
    /// 「子网与该地址空间内已有网络重叠」。它们都是用户改一下输入就能解决的，
    /// 却会落进翻译表的兜底分支变成 1402/500——一次「服务端故障」。
    /// 因此这里单独接住 403 并改写成 1404/409，同时保留守护进程给的原因文本。</para>
    /// </remarks>
    public async Task<NetworkSummary> CreateAsync(
        string hostId,
        CreateNetworkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = DockerResourceName.Require(request.Name, "name", "网络");
        var driver = request.Driver?.Trim();
        var subnet = DockerResourceName.NormalizeIpParameter(request.Subnet, "subnet", true);
        var gateway = DockerResourceName.NormalizeIpParameter(request.Gateway, "gateway", false);
        var ipRange = DockerResourceName.NormalizeIpParameter(request.IpRange, "ipRange", false);
        var labels = DockerResourceName.NormalizeLabels(request.Labels);

        var parameters = new NetworksCreateParameters
        {
            Name = name,

            // 空串而不是 null：SDK 的 Driver 声明为非空可空类型，而 Docker 侧
            // Go 的 encoding/json 把 `"Driver": null` 与「不写该字段」同样处理成零值，
            // 因此两者对守护进程完全等价——用空串就不必抑制可空性检查。
            Driver = driver ?? string.Empty,
            Attachable = request.Attachable ?? false,
            Labels = ToMutable(labels),
        };

        if (subnet is not null || gateway is not null || ipRange is not null)
        {
            parameters.IPAM = new IPAM
            {
                Config =
                [
                    new IPAMConfig
                    {
                        // 三项未填时给空串而非 null：守护进程把空串与「不写该字段」同等看待
                        // （空 Subnet + 空 IPRange 表示「这一池不定范围，由分配器决定」）。
                        Subnet = subnet ?? string.Empty,
                        Gateway = gateway ?? string.Empty,
                        IPRange = ipRange ?? string.Empty,
                    },
                ],
            };

            // IPv6 网段必须显式打开 EnableIPv6，否则守护进程会建出一个「有 IPv6 地址池却不通」的网。
            // 这里按地址族判断而不是让用户手填：手填意味着「填了 IPv6 子网却忘了勾」这种必踩的坑。
            parameters.EnableIPv6 = IsIpv6(subnet) || IsIpv6(gateway) || IsIpv6(ipRange);
        }

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var created = await CreateNetworkAsync(client, parameters, name, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 创建网络 {Name}（驱动 {Driver}，子网 {Subnet}，可单独接入={Attachable}）",
            hostId,
            name,
            string.IsNullOrEmpty(driver) ? "默认" : driver,
            subnet ?? "自动分配",
            request.Attachable ?? false);

        var inspected = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.InspectNetworkAsync(created.ID, cancellationToken),
            $"网络 {name}",
            cancellationToken).ConfigureAwait(false);

        return MapSummary(inspected);
    }

    /// <summary>
    /// 删除网络。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（网络 ID 或名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BusinessException">
    /// 网络标识为空或非法（<c>1000</c>）、网络不存在（<c>1401</c>）、仍有容器接入（<c>1404</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>先 inspect 再删，是为了让报错能说清「谁还在用」</b>。
    /// Docker 对「网络还有活跃端点」报的是 403，转译后只会得到一句
    /// 「Docker API 调用失败（HTTP 403）」——用户完全不知道要先去断开哪个容器。
    /// 而 <c>Containers</c> 里恰好就有容器名，多一次 inspect 换来一条能照做的提示。</para>
    /// <para><b>内置网络（<c>bridge</c> / <c>host</c> / <c>none</c>）拦在服务端</b>：
    /// 删掉它们会波及主机上的<b>所有</b>容器，而且 Docker 自己也会拒绝。
    /// 但守护进程的拒绝文本是英文的，提前拦下能给界面一个可翻译的错误码分支。</para>
    /// </remarks>
    public async Task RemoveAsync(
        string hostId,
        RemoveNetworkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var networkId = DockerResourceName.Require(request.NetworkId, nameof(request.NetworkId), "网络");
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var network = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.InspectNetworkAsync(networkId, cancellationToken),
            $"网络 {networkId}",
            cancellationToken).ConfigureAwait(false);

        GuardRemovable(network);

        var attached = network.Containers?.Keys.ToArray() ?? [];

        if (attached.Length > 0)
        {
            throw new BusinessException(
                ErrorCodes.DockerResourceConflict,
                $"网络「{network.Name}」上还有 {attached.Length} 个容器，先断开或删除它们再删除网络");
        }

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.DeleteNetworkAsync(networkId, cancellationToken),
            $"网络 {networkId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 删除网络 {NetworkId}", hostId, networkId);
    }

    /// <summary>
    /// 调用创建接口，并把 Docker 的 <c>403</c> 改写成 <c>1404</c>。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="parameters">创建参数。</param>
    /// <param name="name">网络名（拼错误消息用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>守护进程返回的创建结果。</returns>
    /// <exception cref="BusinessException">同名网络已存在或子网重叠时为 <c>1404</c>。</exception>
    private static async Task<NetworksCreateResponse> CreateNetworkAsync(
        IDockerClient client,
        NetworksCreateParameters parameters,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.Networks.CreateNetworkAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消：继续抛，框架会当成「请求中止」而不是故障。
            throw;
        }
        catch (Exception exception) when (exception is DockerApiException { StatusCode: HttpStatusCode.Forbidden })
        {
            // 守护进程对「同名已存在」与「子网重叠」都回 403，Message 里带着具体原因。
            // 保留它比换成一句通用文案有用得多——用户据此知道该改名字还是改子网。
            throw new BusinessException(
                ErrorCodes.DockerResourceConflict,
                $"无法创建网络「{name}」：{ForbiddenReason(exception)}",
                exception);
        }
        catch (Exception exception) when (exception is not BusinessException)
        {
            throw DockerErrorTranslator.TranslateException(exception, $"网络 {name}");
        }
    }

    /// <summary>
    /// 判断一个网络是否可以被删除。
    /// </summary>
    /// <param name="network">网络对象。</param>
    /// <exception cref="BusinessException">内置网络受保护（<c>1404</c>）。</exception>
    private static void GuardRemovable(NetworkResponse network)
    {
        if (!BuiltInNetworks.Contains(network.Name))
        {
            return;
        }

        throw new BusinessException(
            ErrorCodes.DockerResourceConflict,
            $"内置网络「{network.Name}」不可删除（它被主机上的容器默认依赖）");
    }

    /// <summary>
    /// 把守护进程返回的网络对象映射成列表项。
    /// </summary>
    /// <param name="network">网络对象。</param>
    /// <returns>列表项。</returns>
    /// <remarks>
    /// <b>刻意不映射容器数</b>：Docker 的列表接口不返回 <c>Containers</c>（只有详情接口返回），
    /// 这里拿到的字典必然为空。补一个恒为 0 的字段进去，只会让界面显示「0 个容器」。
    /// </remarks>
    private static NetworkSummary MapSummary(NetworkResponse network) =>
        new(
            network.ID ?? string.Empty,
            network.Name ?? string.Empty,
            network.Driver ?? string.Empty,
            network.Scope ?? string.Empty,
            network.Attachable,
            network.Internal,
            [.. ReadIpam(network, config => config.Subnet)],
            [.. ReadIpam(network, config => config.Gateway)],
            Copy(network.Labels));

    /// <summary>
    /// 把守护进程返回的网络对象映射成详情投影。
    /// </summary>
    /// <param name="network">网络对象。</param>
    /// <returns>详情。</returns>
    private static NetworkDetail MapDetail(NetworkResponse network)
    {
        var configs = network.IPAM?.Config ?? [];

        return new NetworkDetail(
            network.ID ?? string.Empty,
            network.Name ?? string.Empty,
            network.Driver ?? string.Empty,
            network.Scope ?? string.Empty,
            network.Attachable,
            network.Internal,
            network.EnableIPv4,
            network.EnableIPv6,
            [.. configs.Select(config =>
                new NetworkIpam(Blank(config.Subnet), Blank(config.Gateway), Blank(config.IPRange)))],
            MapEndpoints(network.Containers),
            Copy(network.Options),
            Copy(network.Labels));
    }

    /// <summary>
    /// 把 IPAM 池里的某一项读成字符串列表（跳过空值）。
    /// </summary>
    /// <param name="network">网络对象。</param>
    /// <param name="selector">取值选择器。</param>
    /// <returns>非空的取值列表。</returns>
    private static IEnumerable<string> ReadIpam(NetworkResponse network, Func<IPAMConfig, string?> selector)
    {
        var configs = network.IPAM?.Config;

        if (configs is not { Count: > 0 })
        {
            return [];
        }

        return configs
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
    }

    /// <summary>
    /// 把端点字典映射成列表。
    /// </summary>
    /// <param name="containers">端点字典（键为容器 ID）。</param>
    /// <returns>端点列表；输入为空时返回空列表。</returns>
    /// <remarks>
    /// 按<b>容器 ID</b> 排序而不是让 <c>Dictionary</c> 的枚举顺序说话：
    /// 后者在跨进程 JSON 反序列化后是完全不确定的，
    /// 会让详情页每次刷新时容器顺序都在变。
    /// </remarks>
    private static IReadOnlyList<NetworkContainerEndpoint> MapEndpoints(
        IDictionary<string, EndpointResource>? containers)
    {
        if (containers is not { Count: > 0 })
        {
            return [];
        }

        return
        [
            .. containers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new NetworkContainerEndpoint(
                    pair.Key,
                    pair.Value?.Name ?? string.Empty,
                    Blank(pair.Value?.MacAddress),
                    Blank(pair.Value?.IPv4Address),
                    Blank(pair.Value?.IPv6Address))),
        ];
    }

    /// <summary>
    /// 判断一个 IP / CIDR 参数是否是 IPv6。
    /// </summary>
    /// <param name="value">形如 <c>172.18.0.0/16</c> 或 <c>2001:db8::/64</c> 的值。</param>
    /// <returns>是 IPv6 返回 <see langword="true"/>；无法判定返回 <see langword="false"/>。</returns>
    private static bool IsIpv6(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var addressPart = slash < 0 ? value : value[..slash];

        return IPAddress.TryParse(addressPart, out var address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    /// <summary>
    /// 取守护进程异常里可读的原因文本。
    /// </summary>
    /// <param name="exception">Docker 异常。</param>
    /// <returns>原因文本；没有可用文本时返回通用描述。</returns>
    /// <remarks>
    /// 只取 <see cref="Exception.Message"/> 的<b>首行</b>：Docker 在同一条消息里还会附带
    /// 请求路径与堆栈指引，那些对界面上的用户没有意义，整段贴出来反而淹没真正的原因。
    /// </remarks>
    private static string ForbiddenReason(Exception exception)
    {
        var message = exception.Message;
        var lineBreak = message.IndexOfAny(['\r', '\n']);
        var firstLine = lineBreak < 0 ? message : message[..lineBreak];

        return string.IsNullOrWhiteSpace(firstLine)
            ? "守护进程拒绝了该请求（同名网络已存在，或子网与现有网络重叠）"
            : firstLine.Trim();
    }

    /// <summary>
    /// 取可空字符串规范成「去空白或 <see langword="null"/>」。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>去首尾空白后的值；缺失或全是空白时为 <see langword="null"/>。</returns>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 把守护进程返回的可空字典复制成不可空的只读字典。
    /// </summary>
    /// <param name="source">源字典。</param>
    /// <returns>非 <see langword="null"/> 的字典（内部为可变副本，类型上仍是只读视图）。</returns>
    private static Dictionary<string, string> Copy(IDictionary<string, string>? source) =>
        source is { Count: > 0 }
            ? new Dictionary<string, string>(source, StringComparer.Ordinal)
            : EmptyMap;

    /// <summary>
    /// 把只读字典转成 SDK 参数需要的可变字典。
    /// </summary>
    /// <param name="source">源字典。</param>
    /// <returns>空字典或内容的副本。</returns>
    private static Dictionary<string, string> ToMutable(IReadOnlyDictionary<string, string> source) =>
        source.Count > 0
            ? new Dictionary<string, string>(source, StringComparer.Ordinal)
            : EmptyMap;

    /// <summary>Docker 内置网络的名称，不可删除。</summary>
    /// <remarks>
    /// <para><c>bridge</c> / <c>host</c> / <c>none</c> 是 Docker 固定提供的内置网络，
    /// 删掉任何一个都会波及主机上的<b>所有</b>容器（尤其是 <c>none</c>——
    /// 每个不接入网络的容器都依赖它）。</para>
    /// <para><b>为什么还留着 <c>null</c></b>：Docker 26 之前「禁用网络」叫 <c>null</c>，
    /// 26 起改名成 <c>none</c>。两个都列上才能在新旧守护进程上都拦得住；
    /// 多列一个在当前版本不存在的名字没有任何副作用。</para>
    /// <para>Swarm 模式下还会多出 <c>ingress</c>，但那台机器上本项目本就不提供 Swarm 管理，
    /// 删它同样会被守护进程拒绝——不必在这里重复一遍。</para>
    /// </remarks>
    private static readonly HashSet<string> BuiltInNetworks =
        new(StringComparer.Ordinal) { "bridge", "host", "none", "null" };

    /// <summary>空字典的共享实例。</summary>
    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);
}
