namespace Whalefleet.Application.Networks;

/// <summary>
/// 网络列表请求。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态。校验与夹紧统一在
/// <see cref="NetworkService.ListAsync"/> 里做。
/// </remarks>
public sealed record ListNetworksRequest
{
    /// <summary>
    /// 关键字搜索：按网络名 / 网络 ID / 驱动模糊匹配（不区分大小写）。
    /// </summary>
    /// <remarks>
    /// 在<b>服务端内存</b>里过滤：分页也在服务端做，前端过滤只能筛到当前页，
    /// 规则会与后端漂移。与镜像列表的 <c>search</c> 同口径。
    /// </remarks>
    public string? Search { get; init; }

    /// <summary>页码（从 0 开始）；省略时按 0 处理，负数同样按 0 处理。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数；省略时用默认值（20），超出上限（100）会被夹紧。</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 网络详情请求。
/// </summary>
public sealed record InspectNetworkRequest
{
    /// <summary>网络标识（网络 ID 或网络名）；必填。</summary>
    /// <remarks>
    /// 守护进程的 <c>GET /networks/{id}</c> 两者都接受。
    /// 仍要求满足 <c>^[a-zA-Z0-9][a-zA-Z0-9_.-]*$</c>——
    /// 网络名与卷名共用同一套字符规则，先在本层挡下能让报错更早也更准。
    /// </remarks>
    public string? NetworkId { get; init; }
}

/// <summary>
/// 创建网络请求。
/// </summary>
public sealed record CreateNetworkRequest
{
    /// <summary>网络名；必填。须匹配 <c>^[a-zA-Z0-9][a-zA-Z0-9_.-]*$</c>。</summary>
    public string? Name { get; init; }

    /// <summary>网络驱动；省略时由守护进程按主机默认选择（通常是 <c>bridge</c>）。</summary>
    public string? Driver { get; init; }

    /// <summary>子网（CIDR，如 <c>172.18.0.0/16</c>）；省略时由守护进程从默认地址池分配。</summary>
    /// <remarks>
    /// 显式指定时必须同时指定 <see cref="Gateway"/> 或让它留空由守护进程取网段首地址——
    /// 留空是可以的，守护进程会自己算。只在需要避开与现有网络重叠时才手填。
    /// </remarks>
    public string? Subnet { get; init; }

    /// <summary>网关地址；省略时由守护进程取子网的第一个可用地址。</summary>
    public string? Gateway { get; init; }

    /// <summary>动态分配范围（CIDR，如 <c>172.18.5.0/24</c>）；省略表示整个子网都可分配。</summary>
    public string? IpRange { get; init; }

    /// <summary>是否可被 <c>docker run --network</c> 单独接入；省略为 <see langword="false"/>。</summary>
    /// <remarks>
    /// 对 <c>overlay</c> 驱动是必需的（覆盖网络默认不可单独接入）；
    /// 对 <c>bridge</c> 通常无所谓，但显式打开没有副作用。
    /// </remarks>
    public bool? Attachable { get; init; }

    /// <summary>标签。</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// 删除网络请求。
/// </summary>
public sealed record RemoveNetworkRequest
{
    /// <summary>网络标识（网络 ID 或网络名）；必填。</summary>
    /// <remarks>
    /// 刻意<b>没有 <c>force</c></b>：Docker 的删除网络接口不提供该参数，
    /// 网络只要还有容器接入就必然失败。做一个「看似能强制、实际仍会失败」的开关，
    /// 比没有它更容易让人误以为能删掉。
    /// 要删一个有容器的网络，正确顺序是先把容器从该网络断开（或删掉容器）。
    /// </remarks>
    public string? NetworkId { get; init; }
}

/// <summary>
/// 把容器接入网络请求（对应 <c>docker network connect</c>）。
/// </summary>
/// <remarks>
/// <para><b>幂等</b>：容器已经在该网络上时视为成功，不报错。
/// 理由与批量删除的「重复项先去重」同源——用户点了两次、或两个界面同时操作，
/// 得到的应该是同一个结果，而不是一次莫名其妙的失败。</para>
/// <para><b>容器必须处于运行中</b>：守护进程只允许接入运行中的容器
/// （停止的容器没有网络命名空间可接入）。</para>
/// </remarks>
public sealed record ConnectNetworkRequest
{
    /// <summary>容器标识（容器 ID 或容器名）；必填。</summary>
    public string? ContainerId { get; init; }

    /// <summary>网络标识（网络 ID 或网络名）；必填。</summary>
    public string? NetworkId { get; init; }
}

/// <summary>
/// 把容器从网络断开请求（对应 <c>docker network disconnect</c>）。
/// </summary>
/// <remarks>
/// <para><b>幂等</b>：容器本来就不在该网络上时视为成功（它已经是「断开」的状态）。</para>
/// <para><b>断开会让容器立刻失去该网络的连通性</b>，因此界面必须二次确认。</para>
/// </remarks>
public sealed record DisconnectNetworkRequest
{
    /// <summary>容器标识（容器 ID 或容器名）；必填。</summary>
    public string? ContainerId { get; init; }

    /// <summary>网络标识（网络 ID 或网络名）；必填。</summary>
    public string? NetworkId { get; init; }

    /// <summary>是否强制断开；省略为 <see langword="false"/>。</summary>
    /// <remarks>
    /// <b>强制会连累别人</b>：Docker 的 <c>network disconnect --force</c> 会把
    /// <b>共用同一个网络命名空间</b>的容器一起踢掉——那可能是另一个业务容器。
    /// 因此默认不强制，需要时由用户显式勾选。
    /// </remarks>
    public bool? Force { get; init; }
}
