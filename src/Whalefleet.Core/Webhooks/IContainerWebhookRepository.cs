namespace Whalefleet.Core.Webhooks;

/// <summary>
/// 容器重建 Webhook 仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 与全量列表见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>QueryByIdAsync</c> / <c>QueryAllListAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> /
/// <c>RemoveByIdAsync</c> 由基接口统一声明。</para>
/// <para><b>位置</b>：接口在 <c>Core</c>，实现在 <c>Infrastructure/Persistence/Webhooks</c>。</para>
/// <para><b>「未命中」返回 <see langword="null"/> 而不是抛异常</b>：
/// 「这个令牌无效」要报哪个错误码（<c>1911</c>）由应用服务决定。</para>
/// </remarks>
public interface IContainerWebhookRepository : Common.IRepository<ContainerWebhook, Guid>
{
    /// <summary>
    /// 按「主机 + 容器名」查询。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="containerName">目标容器名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中的 Webhook；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<ContainerWebhook?> FindByHostAndContainerAsync(
        Guid hostId,
        string containerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 Webhook 令牌哈希反查。
    /// </summary>
    /// <param name="tokenHash">令牌的 SHA-256 哈希（小写十六进制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 命中的 Webhook；令牌无效 / 已吊销 / 不存在时返回 <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <b>为什么这里不返回「令牌无效」与「令牌已吊销」的差别</b>：
    /// 两者对外必须表现为同一件事（404），否则未启用与未存在的区分就成了令牌探测工具。
    /// </remarks>
    Task<ContainerWebhook?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某台主机上的全部容器 Webhook。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Webhook 列表（按容器名升序）；没有数据时是空列表。</returns>
    Task<IReadOnlyList<ContainerWebhook>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default);
}
