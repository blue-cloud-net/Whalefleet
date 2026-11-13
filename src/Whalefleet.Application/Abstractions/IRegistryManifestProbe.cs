namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 一次仓库清单探测的结论分类。
/// </summary>
/// <remarks>
/// <b>分类而不是抛异常</b>：探测失败是<b>结果</b>——「仓库要求授权」与
/// 「网络不通」对用户的含义完全不同（前者去配仓库凭据，后者查网络），
/// 而两者都不该让整个扫描中断。
/// </remarks>
public enum RegistryProbeStatus
{
    /// <summary>
    /// 拿到了清单摘要。
    /// </summary>
    Digest,

    /// <summary>
    /// 仓库里没有这个标签（镜像名或标签写错）。
    /// </summary>
    NotFound,

    /// <summary>
    /// 仓库要求授权，且当前凭据不足以访问（含凭据缺失）。
    /// </summary>
    Unauthorized,

    /// <summary>
    /// 被限流（Docker Hub 匿名拉取的主要失败原因）。
    /// </summary>
    RateLimited,

    /// <summary>
    /// 仓库不可达（DNS / 连接 / 超时 / TLS）。
    /// </summary>
    Unreachable,
}

/// <summary>
/// 仓库清单探测结果。
/// </summary>
/// <param name="Status">结论分类。</param>
/// <param name="Digest">清单摘要（<c>sha256:…</c>）；仅在
/// <see cref="RegistryProbeStatus.Digest"/> 时有值。</param>
/// <param name="Message">给用户看的一句话说明；可为空。</param>
public sealed record RegistryProbeResult(
    RegistryProbeStatus Status,
    string? Digest = null,
    string? Message = null)
{
    /// <summary>
    /// 是否拿到了摘要。
    /// </summary>
    public bool Succeeded => this.Status == RegistryProbeStatus.Digest && this.Digest is not null;
}

/// <summary>
/// 仓库清单探测：问仓库「这个标签现在指向哪个摘要」。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 Docker 客户端</b>：Docker 客户端只会跟守护进程说话，
/// 而守护进程的 <c>pull</c> 会<b>真的拉下镜像层</b>——
/// 「检查有没有更新」不该产生几百 MB 流量。这里只发一个 <c>HEAD</c>，
/// 读 <c>Docker-Content-Digest</c> 响应头。</para>
/// <para><b>抽象放在 Application</b>：它抽象的是「某种外部系统」（仓库 HTTP API），
/// 具体实现（Bearer 换令牌 / Basic 凭据 / 镜像加速器）在 Infrastructure。</para>
/// </remarks>
public interface IRegistryManifestProbe
{
    /// <summary>
    /// 探测某个标签当前的清单摘要。
    /// </summary>
    /// <param name="repository">仓库名（可含仓库主机前缀，如
    /// <c>harbor.local:5000/app</c>）。</param>
    /// <param name="tag">标签（如 <c>latest</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结论；<b>不抛</b>「不可达 / 需授权 / 被限流」这类异常。</returns>
    Task<RegistryProbeResult> ProbeAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken = default);
}
