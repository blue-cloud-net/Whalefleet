namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// Docker 客户端缓存的运维操作。
/// </summary>
/// <remarks>
/// <para>刻意与 <see cref="Application.Abstractions.IDockerClientFactory"/> 分开：
/// 业务代码只需取客户端，不该感知缓存回收；而后台回收任务也不应依赖整个工厂接口。</para>
/// <para>实现由 <see cref="DockerClientFactory"/> 一并提供，注册为同一单例。</para>
/// </remarks>
public interface IDockerClientCacheMaintenance
{
    /// <summary>
    /// 回收所有空闲超时的客户端。
    /// </summary>
    /// <returns>本次实际回收的条目数（<c>0</c> 表示无可回收项或回收已被禁用）。</returns>
    int ReapIdleClients();
}
