namespace Docker.Manager.Core.Permissions;

/// <summary>
/// 用户-容器授权仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>落地阶段</b>：0.3.0 —— 本迭代仅定义接口与实体，<b>尚无实现、不建表、不注册 DI</b>。</para>
/// <para><b>查询必须带主机</b>：容器标识只在单台守护进程内唯一，所有按键查询都要求
/// <c>hostId</c> 与 <c>containerId</c> 成对出现。</para>
/// </remarks>
public interface IUserContainerPermissionRepository
{
    /// <summary>
    /// 查询某用户对某主机上某个容器的授权。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="hostId">主机主键。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权记录；不存在或已撤销时返回 <see langword="null"/>。</returns>
    Task<UserContainerPermission?> GetAsync(
        Guid userId,
        Guid hostId,
        string containerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某用户的全部有效容器授权。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权列表（可能为空）。</returns>
    Task<IReadOnlyList<UserContainerPermission>> ListByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某主机上全部有效的容器授权。
    /// </summary>
    /// <param name="hostId">主机主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权列表（可能为空）。</returns>
    Task<IReadOnlyList<UserContainerPermission>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增授权，并写入创建审计。
    /// </summary>
    /// <param name="permission">待新增的授权记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="permission"/> 为 <see langword="null"/>。</exception>
    Task AddAsync(UserContainerPermission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新授权（权限位变更），并写入修改审计。
    /// </summary>
    /// <param name="permission">待更新的授权记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="permission"/> 为 <see langword="null"/>。</exception>
    Task UpdateAsync(UserContainerPermission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销授权（软删除），并写入删除审计。
    /// </summary>
    /// <param name="id">授权记录主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
