using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Application.Hosts;

/// <summary>
/// 主机列表项（管理端点的返回契约）。
/// </summary>
/// <param name="Id">主机主键（<see cref="Guid"/>，仅用于权限外键与审计，不进 URL）。</param>
/// <param name="Name">主机名——对外标识，也是路径里的 <c>hostId</c>。</param>
/// <param name="Type">连接方式。</param>
/// <param name="Endpoint">端点原文。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Reachable">
/// 连通状态：<see langword="true"/> 可连、<see langword="false"/> 连不上、
/// <see langword="null"/> 表示**未探测**（创建 / 更新后返回，避免写操作被网络超时拖住）。
/// </param>
/// <param name="AuthKind">认证方式（用于界面展示"这台机器带不带凭据"，<b>不含任何凭据内容</b>）。</param>
/// <param name="GroupId">所属分组主键；<see langword="null"/> 表示未分组。</param>
/// <remarks>
/// <para><b>为什么不直接返回实体</b>：<c>DockerHost</c> 带审计字段、凭据投影与领域方法，
/// 直接序列化既泄漏内部结构，也会把「凭据」这类敏感字段带出去。</para>
/// <para><b><see cref="Reachable"/> 为可空布尔</b>：用 <c>false</c> 表示"没探测"会让界面显示
/// 「离线」，把一次未探测误报成故障；<c>null</c> 才能让界面显示「未知」。</para>
/// </remarks>
public sealed record HostSummary(
    Guid Id,
    string Name,
    DockerHostType Type,
    string Endpoint,
    bool Enabled,
    bool? Reachable,
    DockerHostAuthKind AuthKind,
    Guid? GroupId);
