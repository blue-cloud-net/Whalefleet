using Docker.DotNet;
using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 主机访问器（<b>Scoped</b>）：把主机标识解析成 <c>hosts</c> 表里的主机，
/// 并据此取得 Docker 客户端。业务代码取客户端的<b>唯一入口</b>。
/// </summary>
/// <remarks>
/// <para><b>主机来源是数据库，不再是配置</b>（0.0.8 起）：本接口从 <c>IHostRepository</c>
/// 读取主机，因此「配置里的主机」与「数据库里的主机」不会再有两份真相。</para>
/// <para><b>为什么是 Scoped</b>：它依赖 <c>IHostRepository</c>（依赖当前请求的用户上下文）。
/// 客户端池是 Singleton（客户端必须跨请求存活），二者生命周期不同，故分属两层——
/// 本接口只负责「解析 + 转发」，<b>不</b>持有任何客户端缓存。</para>
/// <para><b>主机标识即主机名</b>：<paramref name="hostId"/> 是 <c>DockerHost.Name</c>
/// （不区分大小写，由数据库部分唯一索引保证唯一）。<c>DockerHost.Id</c>（<see cref="Guid"/>）
/// 只用于权限外键与审计，<b>不</b>出现在 URL / Header 中。</para>
/// <para><b>后续演进</b>：0.3.0 起增加资源可见性校验，接在 Api 的
/// <c>CurrentHostMiddleware</c>（从路由值 <c>hostId</c> 解析当前主机）之后；
/// 本接口本身保持「解析 + 转发」这两件事。</para>
/// </remarks>
public interface IDockerHostAccessor
{
    /// <summary>
    /// 解析主机标识对应的主机实体。
    /// </summary>
    /// <param name="hostId">主机标识（即主机名，不区分大小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机实体（供权限校验等场景取用 <c>Id</c>）。</returns>
    /// <exception cref="Common.BusinessException">主机不存在或已软删除（错误码 <c>1300</c>）。</exception>
    Task<DockerHost> ResolveAsync(string hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定主机对应的 Docker 客户端。
    /// </summary>
    /// <remarks>
    /// 返回的客户端由客户端池管理并跨请求复用（因此「切换主机不重建连接」），
    /// 调用方<b>不得</b>自行释放。
    /// </remarks>
    /// <param name="hostId">主机标识（即主机名，不区分大小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该主机对应的 <see cref="IDockerClient"/>（归客户端池管理）。</returns>
    /// <exception cref="Common.BusinessException">
    /// 主机不存在（<c>1300</c>）、主机已停用（<c>1302</c>），或端点配置非法（<c>1301</c>）。
    /// </exception>
    Task<IDockerClient> GetClientAsync(string hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试连通性：新建一个<b>临时</b>客户端并 Ping，随后立即释放（<b>不</b>进入客户端池）。
    /// </summary>
    /// <remarks>
    /// 入参是连接描述而非主机标识，因此也能测试「尚未保存的主机配置」
    /// （对应 <c>POST /api/v1/host/test</c>）。
    /// </remarks>
    /// <param name="connection">待测试的 Docker 主机连接描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连通返回 <c>true</c>；端点非法、守护进程不可达等情况一律返回 <c>false</c>（不抛异常）。</returns>
    Task<bool> TestConnectionAsync(DockerHostConnection connection, CancellationToken cancellationToken = default);
}
