namespace Docker.Manager.Core.Common;

/// <summary>
/// 仓储的共同持久化契约（CRUD 部分）。
/// </summary>
/// <typeparam name="TEntity">实体类型（须继承 <see cref="Entity{TId}"/>）。</typeparam>
/// <typeparam name="TId">实体主键类型。</typeparam>
/// <remarks>
/// <para><b>只放真正共同的成员</b>：四个 CRUD 方法与全量列表查询在所有仓储里同构，因此统一在此声明。
/// 各仓储特有的查询（按用户名 / 按令牌哈希 / 按名称 / <b>分页</b> / 计数 / 改默认角色）
/// 留在具体接口里——把它们硬塞进基类只会得到一堆"本仓储不支持"的空实现。</para>
/// <para><b>方法的形状是刻意统一的，不是巧合</b>：</para>
/// <list type="bullet">
///   <item><description>写入类（<see cref="AddAsync"/>）不返回行数——插入成功即成功，
///   唯一键冲突会直接抛底层异常，不存在"插了 0 行"的中间态；</description></item>
///   <item><description>更新与删除返回 <see cref="bool"/>（是否命中一行），<b>不抛</b>"未找到"异常——
///   是否把 <c>false</c> 转成 <c>1201</c> / <c>1300</c> 等错误码由应用服务决定，
///   仓储不承担错误码语义；</description></item>
///   <item><description>软删除过滤与审计填充由实现负责（<c>Infrastructure/Persistence/RepositoryBase</c>
///   已统一提供），本接口不重复声明；两个读取方法的 <c>WHERE</c> 与 <c>ORDER BY</c> 同样由实现给全
///   （<c>refresh_tokens</c> 就没有 <c>is_deleted</c> 列，基类无从代为拼装）；</description></item>
/// </list>
/// <para><b>删除语义因实体而异</b>：软删除实体（<c>User</c> / <c>DockerHost</c> / <c>Role</c>）
/// 的 <see cref="RemoveByIdAsync"/> 是打标记；不参与软删除的实体（如 <c>RefreshToken</c>）
/// 则为物理删除。具体语义由实现决定并在具体接口上注明。</para>
/// </remarks>
public interface IRepository<TEntity, in TId>
    where TEntity : Entity<TId>
    where TId : notnull
{
    /// <summary>
    /// 按主键查询实体。
    /// </summary>
    /// <param name="id">实体主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实体；不存在（或已软删除）时返回 <see langword="null"/>。</returns>
    Task<TEntity?> QueryByIdAsync(TId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出全部实体（<b>顺序由实现决定</b>，本契约不保证任何排序）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实体列表；没有数据时是<b>空列表</b>而非 <see langword="null"/>。</returns>
    /// <remarks>
    /// 实现应默认过滤已软删除的行；不参与软删除的实体（如刷新令牌）则不带该过滤。
    /// 需要分页时<b>不要</b>用它——分页的形状（<c>skip</c> / <c>take</c> 与参数校验）因仓储而异，
    /// 留在具体接口（如 <c>IUserRepository.ListAsync</c>）。
    /// </remarks>
    Task<IReadOnlyList<TEntity>> QueryAllListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增实体，并写入创建审计。
    /// </summary>
    /// <param name="entity">待新增的实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> 为 <see langword="null"/>。</exception>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新实体，并写入修改审计。
    /// </summary>
    /// <param name="entity">待更新的实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实更新了 1 行返回 <see langword="true"/>；实体不存在（或已软删除）返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> 为 <see langword="null"/>。</exception>
    Task<bool> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除实体，并写入删除审计。
    /// </summary>
    /// <param name="id">实体主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实删除了 1 行返回 <see langword="true"/>；实体不存在（或已软删除）返回 <see langword="false"/>。</returns>
    /// <remarks>幂等：对已软删除的实体再次调用不会报错，只是返回 <see langword="false"/>。</remarks>
    Task<bool> RemoveByIdAsync(TId id, CancellationToken cancellationToken = default);
}
