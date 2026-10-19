namespace Whalefleet.Application.Networks;

/// <summary>
/// 网络列表请求。
/// </summary>
/// <remarks>
/// <b>刻意为空</b>：列表端点不接受请求体（见 <see cref="NetworkListResult"/>）。
/// 留一个空类型而不是删掉，是为了让控制器签名可读，
/// 也为 <c>0.16.0</c> 接入 <c>search</c> / <c>sortBy</c> 时不必改路由或方法名。
/// </remarks>
public sealed record ListNetworksRequest
{
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
