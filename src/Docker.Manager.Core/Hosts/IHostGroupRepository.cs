namespace Docker.Manager.Core.Hosts;

/// <summary>
/// 主机分组仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 与全量列表见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>QueryByIdAsync</c> / <c>QueryAllListAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> /
/// <c>RemoveByIdAsync</c> 由基接口统一声明。</para>
/// <para><b>位置</b>：接口在 <c>Core</c>，实现在 <c>Infrastructure/Persistence</c>。</para>
/// <para><b>软删除语义</b>：读取默认过滤已软删除的分组；按主键查中软删除行按「不存在」处理。</para>
/// <para><b>不负责主机归属</b>：把组内主机改成「未分组」是跨聚合的操作（涉及主机实体的审计字段），
/// 因此由应用服务在删除分组前逐台调用 <see cref="IHostRepository.UpdateAsync"/> 完成，
/// 而不是在分组仓储里写一条绕过审计的批量 UPDATE。分组数量少、组内主机也少，这点代价换来的是
/// 「所有实体变更都经过同一套审计填充」。</para>
/// </remarks>
public interface IHostGroupRepository : Common.IRepository<HostGroup, Guid>
{
    /// <summary>
    /// 按分组名查询（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="name">分组名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分组；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<HostGroup?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断分组名是否已被占用（不区分大小写，忽略已软删除的分组）。
    /// </summary>
    /// <param name="name">分组名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);
}
