using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// 一个 Docker 主机的连接描述——工厂方法的统一入参形态。
/// </summary>
/// <remarks>
/// <para><b>与持久化模型解耦</b>：刻意不叫 <c>Host</c>。0.0.8 落地 <c>hosts</c> 表后，
/// 由 <c>Host</c> 聚合根映射出本对象，工厂无需感知仓储与数据库；
/// 而「测试一个尚未保存的主机」（<c>POST /api/v1/host/test</c>）也能直接构造本对象。</para>
/// <para><b>身份 ≠ 缓存键</b>：<see cref="HostId"/> 只用于日志与配置查找，<b>不参与</b>缓存键。
/// 缓存键由端点的规范化结果（必要时再拼上
/// <see cref="DockerHostAuth.CacheDiscriminator"/>）决定，因此两个指向同一守护进程的
/// 主机名会共享同一个客户端与其 HTTP 连接池——这正是「切换主机不重建连接」的前提。</para>
/// </remarks>
public sealed record DockerHostConnection
{
    /// <summary>
    /// 主机标识——即 <c>hosts</c> 表里的主机名（不区分大小写）。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>用 <c>Guid</c> 主键：主机名唯一、可读，能直接进 URL 与 <c>X-Host-Id</c> 头；
    /// 主键只用于权限外键与审计。
    /// </remarks>
    public required string HostId { get; init; }

    /// <summary>
    /// 连接方式。
    /// </summary>
    public required DockerHostType Type { get; init; }

    /// <summary>
    /// 端点。socket 形如 <c>/var/run/docker.sock</c>（也接受 <c>unix://</c> / <c>npipe://</c> 前缀），
    /// TCP 形如 <c>tcp://host:2375</c>（缺省端口 2375）。
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// 认证信息（当前仅支持 <see cref="DockerHostAuth.None"/>）。
    /// </summary>
    public DockerHostAuth Auth { get; init; } = DockerHostAuth.None;

    /// <summary>
    /// 由 <c>DockerHost</c> 实体映射出连接描述。
    /// </summary>
    /// <param name="host">主机实体。</param>
    /// <returns>连接描述；<see cref="HostId"/> 取主机名（<c>Name</c>）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> 为 <see langword="null"/>。</exception>
    public static DockerHostConnection From(DockerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return new DockerHostConnection
        {
            HostId = host.Name,
            Type = host.Type,
            Endpoint = host.Endpoint.Value,
            Auth = DockerHostAuth.From(host.Credentials),
        };
    }
}
