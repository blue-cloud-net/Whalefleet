namespace Whalefleet.Core.Permissions;

/// <summary>
/// 用户-主机授权仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>落地阶段</b>：0.3.0 —— 本迭代仅定义接口与实体，<b>尚无实现、不建表、不注册 DI</b>。</para>
/// <para><b>软删除语义</b>：读取方法默认过滤已撤销（软删除）的授权。</para>
/// </remarks>
public interface IUserHostPermissionRepository
{
    /// <summary>
    /// 查询某用户对某主机的授权。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="hostId">主机主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权记录；不存在或已撤销时返回 <see langword="null"/>。</returns>
    Task<UserHostPermission?> GetAsync(Guid userId, Guid hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某用户的全部有效主机授权。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权列表（可能为空）。</returns>
    Task<IReadOnlyList<UserHostPermission>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某主机的全部有效授权（供管理界面查看"谁能访问这台机器"）。
    /// </summary>
    /// <param name="hostId">主机主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权列表（可能为空）。</returns>
    Task<IReadOnlyList<UserHostPermission>> ListByHostAsync(Guid hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增授权，并写入创建审计。
    /// </summary>
    /// <param name="permission">待新增的授权记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="permission"/> 为 <see langword="null"/>。</exception>
    Task AddAsync(UserHostPermission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新授权（权限位变更），并写入修改审计。
    /// </summary>
    /// <param name="permission">待更新的授权记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="permission"/> 为 <see langword="null"/>。</exception>
    Task UpdateAsync(UserHostPermission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销授权（软删除），并写入删除审计。
    /// </summary>
    /// <param name="id">授权记录主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
