using Docker.DotNet;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 客户端工厂抽象：<b>按连接</b>缓存客户端，供多主机切换复用。
/// </summary>
/// <remarks>
/// <para>位于应用层而非领域层：DockerClient 是对外部资源（Docker 守护进程）的抽象，
/// 不是领域概念。领域层的「DockerHost 聚合根」才是"管理 Docker 主机"的领域知识。</para>
/// <para>实现位于 <c>Infrastructure/Docker/DockerClientFactory</c>，基于
/// <see href="https://github.com/testcontainers/Docker.DotNet">Docker.DotNet.Enhanced</see>。</para>
/// <para><b>缓存键</b>由端点的规范化连接串（必要时再拼上认证指纹）决定，<b>不是</b>
/// <see cref="DockerHostConnection.HostId"/>。因此多个主机名指向同一守护进程时共享同一个客户端；
/// 反之某主机的端点被改动后应调用 <see cref="Invalidate"/> 让新配置立即生效。</para>
/// <para><b>连接保持</b>：同一连接的客户端在进程内长期驻留（因而 HTTP 连接池可复用），
/// 仅在显式 <see cref="Invalidate"/> 或空闲超时回收时才释放。
/// 调用方<b>不得</b>自行 <c>Dispose</c> 返回的客户端——其生命周期归工厂管理。</para>
/// <para><b>后续演进</b>：0.0.8 由 <c>IDockerHostAccessor</c>（请求 Header <c>X-Host-Id</c>）
/// 把 <c>hostId</c> 的来源从配置切换为 <c>hosts</c> 表；本接口无需再变。</para>
/// </remarks>
public interface IDockerClientFactory
{
    /// <summary>
    /// 获取（必要时构建）指定主机的 Docker 客户端，并把它标记为「刚刚使用」。
    /// </summary>
    /// <param name="hostId">主机标识（对应配置 <c>Docker:Hosts</c> 的 <c>Name</c>，不区分大小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该连接对应的 <see cref="IDockerClient"/>（归工厂管理，调用方不要释放）。</returns>
    /// <exception cref="Common.BusinessException">
    /// 主机不存在（错误码 <c>1300</c>），或端点配置非法导致无法构建客户端（错误码 <c>1301</c>）。
    /// </exception>
    Task<IDockerClient> GetClientAsync(string hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试连通性：构建一个<b>临时</b>客户端并 Ping，随后立即释放（<b>不</b>进入缓存）。
    /// </summary>
    /// <remarks>
    /// 入参是连接描述而非 <c>hostId</c>，因此也能测试「尚未保存的主机配置」
    /// （对应 <c>POST /api/v1/host/test</c>）。
    /// </remarks>
    /// <param name="connection">待测试的 Docker 主机连接描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连通返回 <c>true</c>；端点非法、守护进程不可达等情况一律返回 <c>false</c>（不抛异常）。</returns>
    Task<bool> TestConnectionAsync(DockerHostConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使指定主机对应的缓存客户端失效：移出缓存并释放。
    /// </summary>
    /// <remarks>
    /// <para>用于主机配置变更（端点 / 凭据改动）或主机被删除之后。</para>
    /// <para>失效粒度是<b>连接</b>而非主机：若另有主机名指向同一端点，它的客户端会被一并释放、
    /// 并在下次取用时重建（二者本就是同一个守护进程的连接）。</para>
    /// <para>对未知 <paramref name="hostId"/> 或尚未缓存的连接是幂等空操作（记 Debug 日志）。</para>
    /// </remarks>
    /// <param name="hostId">主机标识。</param>
    void Invalidate(string hostId);
}
