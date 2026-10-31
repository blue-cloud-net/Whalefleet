namespace Whalefleet.Core.Stacks;

/// <summary>
/// 编排 Stack 仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 与全量列表见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>QueryByIdAsync</c> / <c>QueryAllListAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> /
/// <c>RemoveByIdAsync</c> 由基接口统一声明。</para>
/// <para><b>位置</b>：接口在 <c>Core</c>，实现在 <c>Infrastructure/Persistence/Stacks</c>。</para>
/// <para><b>「未命中」返回 <see langword="null"/> 而不是抛异常</b>：
/// 「这个 Stack 不存在」要报哪个错误码（<c>1900</c>）由应用服务决定，
/// 仓储只负责如实回答「没有这一行」。</para>
/// </remarks>
public interface IStackRepository : Common.IRepository<Stack, Guid>
{
    /// <summary>
    /// 按「主机 + 名称」查询。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="name">Stack 名称（区分大小写——Compose 项目名本身只允许小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Stack；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<Stack?> FindByHostAndNameAsync(
        Guid hostId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某台主机上的全部 Stack。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Stack 列表（按名称升序）；没有数据时是空列表。</returns>
    Task<IReadOnlyList<Stack>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 Webhook 令牌哈希反查 Stack。
    /// </summary>
    /// <param name="tokenHash">令牌的 SHA-256 哈希（小写十六进制）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中的 Stack；令牌无效 / 已吊销 / 不存在时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么这里不返回「令牌无效」与「令牌已吊销」的差别</b>：
    /// 两者对外必须表现为同一件事（404），否则未启用与未存在的区分就成了令牌探测工具。
    /// 应用服务据此统一报 <c>1906</c>。
    /// </remarks>
    Task<Stack?> FindByWebhookTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);
}
