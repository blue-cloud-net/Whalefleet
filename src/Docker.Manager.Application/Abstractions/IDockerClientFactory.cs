using Docker.DotNet;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// Docker 客户端工厂抽象。
/// </summary>
/// <remarks>
/// <para>位于应用层而非领域层：DockerClient 是对外部资源（Docker 守护进程）的抽象，
/// 不是领域概念。领域层将来落地的"DockerHost 聚合根"才是"管理 Docker 主机"的领域知识。</para>
/// <para>实现位于 <c>Infrastructure/Docker/DockerClientFactory</c>，默认基于
/// <see href="https://github.com/testcontainers/Docker.DotNet">Docker.DotNet.Enhanced</see>。</para>
/// <para>0.0.1 仅暴露 <see cref="GetClientAsync"/>；多主机场景（0.0.8）扩展为
/// <c>GetClientAsync(string hostId)</c>，并通过 <c>IDockerHostAccessor</c> 从请求 Header 解析。</para>
/// </remarks>
public interface IDockerClientFactory
{
    /// <summary>
    /// 获取 Docker 客户端实例（懒加载，第一次调用时构建）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置完成的 <see cref="DockerClient"/>。</returns>
    Task<DockerClient> GetClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试连通性，不缓存返回的客户端。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连通成功返回 <c>true</c>，否则 <c>false</c>。</returns>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}