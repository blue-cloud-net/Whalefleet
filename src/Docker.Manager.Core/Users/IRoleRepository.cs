namespace Docker.Manager.Core.Users;

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