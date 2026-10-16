namespace Whalefleet.Core.Users;

/// <summary>
/// 角色仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>角色是运行时数据</b>：管理员可在系统运行中新增 / 修改 / 删除自定义角色，
/// 因此本接口没有"角色集合是固定的"这类假设。</para>
/// <para><b>全量列表见基接口</b>：<c>QueryAllListAsync</c>（顺序：内置角色在前，再按角色名）。</para>
/// <para><b>软删除语义</b>：读取方法默认过滤已删除角色（<c>WHERE is_deleted = 0</c>）；
/// 角色名唯一索引带 <c>WHERE is_deleted = 0</c>，删除后同名可再次创建。</para>
/// <para><b>内置角色不可删</b>：由 <see cref="Role.Retire"/>（充血）与
/// <c>RemoveAsync</c>（仓储层 <c>WHERE is_built_in = 0</c>）双重保证，
/// 后者是数据层兜底，无法被绕过。</para>
/// <para><b>删除会清默认标记</b>：删除角色时同步置 <c>is_default = 0</c>，
/// 否则默认角色被删后「创建用户取默认角色」就悬空了。</para>
/// </remarks>
public interface IRoleRepository : Common.IRepository<Role, Guid>
{
    /// <summary>
    /// 按角色标识查询（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="name">角色标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键集合批量查询角色。
    /// </summary>
    /// <param name="ids">角色主键集合；为空时直接返回空列表（不发 SQL）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>查到的角色；主键不存在或已软删除的会被<b>静默跳过</b>（不报错、不补空位）。</returns>
    /// <remarks>
    /// 存在的原因是分配列表的展示字段补齐：一条列表 N 行，逐行 <c>QueryByIdAsync</c>
    /// 就是 N 次往返。批量查一次在内存里映射是这里唯一的合理写法。
    /// </remarks>
    Task<IReadOnlyList<Role>> QueryListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断角色标识是否已被占用（不区分大小写，忽略已软删除的角色）。
    /// </summary>
    /// <param name="name">角色标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询当前默认角色（<see cref="Role.IsDefault"/> 为真的那一个）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>默认角色；正常情况下恒有值（迁移已种入），无则返回 <see langword="null"/>。</returns>
    /// <remarks>创建用户时未指定角色即采用它；查不到属于数据异常，应用服务应显式报错而非静默兜底。</remarks>
    Task<Role?> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询某个角色的权限集（规范化、已排序）。
    /// </summary>
    /// <param name="roleId">角色主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限标识列表；角色存在但一个权限都没有时返回<b>空列表</b>（不是 <see langword="null"/>）。</returns>
    /// <remarks>
    /// 权限存于 <c>role_permissions</c> 表而非 <c>roles</c> 的列，因此读取需要独立的联表查询。
    /// 鉴权路径上通常需要<b>多个</b>角色的权限（一次请求里同一人可能持多个角色），
    /// 实现应保证单角色查询不产生 N+1——调用方按需自行批量化。
    /// </remarks>
    Task<IReadOnlyList<string>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 覆盖式替换某个角色的权限集（一个事务内完成「差量写入」）。
    /// </summary>
    /// <param name="roleId">角色主键。</param>
    /// <param name="permissions">新的权限集（须已规范化或在实现内规范化）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实替换成功返回 <see langword="true"/>；角色不存在或已软删除返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// <b>为什么是「覆盖」而不是「增删」</b>：权限矩阵的提交天然是「我勾了这些」，
    /// 由调用方算出差量会把同一条规则复制到每一个写入点。
    /// <b>调用方须先校验内置角色</b>（<see cref="Role.SetPermissions"/> 会抛
    /// <see cref="InvalidOperationException"/>，但那是应用层的职责——仓储只管落库）。
    /// </remarks>
    Task<bool> ReplacePermissionsAsync(
        Guid roleId,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计未删除的角色总数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色数量。</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);



    /// <summary>
    /// 把指定角色设为默认角色：<b>同一事务内</b>先清掉其他角色的默认标记，再标记本角色。
    /// </summary>
    /// <param name="id">角色主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实更新了 1 行返回 <see langword="true"/>；角色不存在或已软删除返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 「至多一个默认角色」这条不变量由本方法保证（而不是 <see cref="Role.MakeDefault"/>）：
    /// 单独调实体方法只改一行，并列的默认角色会让"创建用户取默认角色"变得不确定。
    /// </remarks>
    Task<bool> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);

}