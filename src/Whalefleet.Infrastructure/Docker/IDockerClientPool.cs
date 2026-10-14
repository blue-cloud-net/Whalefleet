using Docker.DotNet;
using Whalefleet.Application.Abstractions;

namespace Whalefleet.Infrastructure.Docker;

/// <summary>
/// Docker 客户端池：按「规范化连接串」复用客户端，并负责其生命周期。
/// </summary>
/// <remarks>
/// <para><b>为什么叫池而不是缓存</b>：池里装的是<b>操作系统资源</b>（socket + 后台线程 +
/// 底层 HTTP 连接池），而复用它们正是本类型存在的理由——重建客户端的代价（丢掉已建立的
/// 连接与连接池）恰恰是它要避免的。因此它具备收缩空闲
/// （<see cref="IDockerClientPoolMaintenance.ReapIdleClients"/>）、天然容量上限（= 主机数）
/// 与集中释放（<see cref="IDisposable.Dispose"/>）——都是「池」的特征。</para>
/// <para><b>契约：只借不还。</b>调用方借用（<see cref="GetOrCreate"/>）之后<b>不得</b>
/// 自行释放客户端，其生命周期归本池管理（与 <c>IHttpClientFactory</c> 同一套路，
/// 因此不构成「泄漏」）。</para>
/// <para><b>按连接而非按主机池化</b>：多个主机名指向同一守护进程时共享同一个客户端与其
/// HTTP 连接池——这正是「切换主机不重建连接」的前提。</para>
/// <para><b>入参是连接描述而非主机标识</b>：池不查数据库（那是 <c>IDockerHostAccessor</c> 的
/// 职责），因此池可注册为 Singleton 而不构成「捕获依赖」。</para>
/// </remarks>
public interface IDockerClientPool
{
    /// <summary>
    /// 取得（必要时创建）该连接的客户端，并把它标记为「刚刚使用」。
    /// </summary>
    /// <param name="connection">连接描述。</param>
    /// <returns>客户端（归池管理，调用方<b>不要</b>释放）。</returns>
    /// <exception cref="Common.BusinessException">端点无法规范化（错误码 <c>1301</c>）。</exception>
    /// <exception cref="ObjectDisposedException">池已被释放。</exception>
    IDockerClient GetOrCreate(DockerHostConnection connection);

    /// <summary>
    /// 使该连接对应的客户端失效：移出池并释放。
    /// </summary>
    /// <remarks>
    /// <para>用于主机配置变更（端点 / 凭据改动）或主机被删除之后。</para>
    /// <para>失效粒度是<b>连接</b>而非主机：若另有主机名指向同一端点，它的客户端会被一并释放、
    /// 并在下次取用时重建（二者本就是同一个守护进程的连接）。</para>
    /// <para>对尚未池化的连接是幂等空操作；连接描述非法时记 Debug 日志后返回。</para>
    /// </remarks>
    /// <param name="connection">连接描述（须为变更<b>前</b>的旧值，否则算不出原池键）。</param>
    void Invalidate(DockerHostConnection connection);
}
