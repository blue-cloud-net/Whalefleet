namespace Docker.Manager.Core.Hosts;

/// <summary>
/// Docker 主机仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 与全量列表见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>QueryByIdAsync</c> / <c>QueryAllListAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> /
/// <c>RemoveByIdAsync</c> 由基接口统一声明（全量列表顺序：创建时间升序，同时间再按名称）。</para>
/// <para><b>位置</b>：接口定义在 <c>Core</c>，实现在 <c>Infrastructure/Persistence</c>。</para>
/// <para><b>软删除语义</b>：读取方法<b>默认过滤</b>已软删除的主机（<c>WHERE is_deleted = 0</c>）；
/// 按主键查中软删除行按「不存在」处理（返回 <see langword="null"/>）。<see cref="RemoveAsync"/> 也是软删除。</para>
/// <para><b>名称唯一性</b>：由数据库部分唯一索引保证（不区分大小写）；<see cref="ExistsByNameAsync"/>
/// 只用于提前给出友好错误。</para>
/// <para><b>与 Docker 客户端的关系</b>：仓储只负责持久化，<b>不</b>返回任何客户端或连接对象；
/// 取客户端一律走 <c>IDockerClientFactory</c>（见 <c>docs/architecture.md</c> 的反模式清单）。</para>
/// </remarks>
public interface IHostRepository : Common.IRepository<DockerHost, Guid>
{
    /// <summary>
    /// 按显示名查询主机（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="name">主机显示名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<DockerHost?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断显示名是否已被占用（不区分大小写，忽略已软删除的主机）。
    /// </summary>
    /// <param name="name">主机显示名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计未删除的主机数量（启动引导用它判断是否需要种入默认主机）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机数量。</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询某个分组下的全部主机。
    /// </summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该分组的主机（顺序同全量列表：创建时间升序，同时间再按名称）。</returns>
    /// <remarks>
    /// 删除分组前需要把组内主机逐台改成未分组。刻意<b>不</b>提供「一条 UPDATE 批量置空」：
    /// 那会绕过 <c>RepositoryBase</c> 的审计填充（<c>last_modification_time</c> 等），
    /// 让「谁在什么时候把主机挪出了分组」永久丢失。分组与组内主机的数量都是个位数到两位数，
    /// 逐台更新的代价可以忽略。
    /// </remarks>
    Task<IReadOnlyList<DockerHost>> QueryByGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计各分组下的主机数（分组列表用它显示「N 台」，避免前端自己遍历）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分组主键 → 主机数；未分组的主机不在结果里。</returns>
    Task<IReadOnlyDictionary<Guid, int>> CountByGroupAsync(CancellationToken cancellationToken = default);
}
