using Docker.DotNet;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 客户端工厂抽象：按连接描述<b>新建</b>一个 Docker 客户端。
/// </summary>
/// <remarks>
/// <para><b>只负责创建，不做任何缓存</b>：本接口是无状态的纯构建器。客户端的<b>复用</b>
/// 与生命周期管理由 <c>IDockerClientPool</c>（Infrastructure）承担——它按「规范化连接串」
/// （必要时再拼认证区分串）缓存客户端，使多个指向同一守护进程的主机名共享同一个客户端
/// 与其 HTTP 连接池。</para>
/// <para><b>为什么要拆开</b>：原先「创建 + 缓存 + 回收」挤在一个类里，既让工厂承担了池的职责，
/// 也让「连接存活判定」这种只有持有缓存条目才做得了的事混进了构建逻辑。拆开后本接口的语义
/// 就是它的名字：给一份连接描述，还一个新建的客户端。</para>
/// <para>位于应用层而非领域层：DockerClient 是对外部资源（Docker 守护进程）的抽象，
/// 不是领域概念。实现位于 <c>Infrastructure/Docker/DockerClientFactory</c>，基于
/// <see href="https://github.com/testcontainers/Docker.DotNet">Docker.DotNet.Enhanced</see>。</para>
/// <para><b>调用方注意</b>：每次调用都会新建一个客户端（连同新的 HTTP 连接池），且生命周期
/// <b>归调用方</b>（用完须自行释放）。常规取用请走 <c>IDockerHostAccessor</c>；
/// 仅在需要一次性客户端（如连通性测试）时才直接调用本接口。</para>
/// </remarks>
public interface IDockerClientFactory
{
    /// <summary>
    /// 按连接描述新建一个 Docker 客户端。
    /// </summary>
    /// <param name="connection">连接描述（连接方式 + 端点 + 认证）。</param>
    /// <returns>新建的 <see cref="IDockerClient"/>，生命周期归调用方。</returns>
    /// <exception cref="Common.BusinessException">端点无法规范化（错误码 <c>1301</c>）。</exception>
    IDockerClient CreateClient(DockerHostConnection connection);
}
