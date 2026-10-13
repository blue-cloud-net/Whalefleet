namespace Docker.Manager.Core.Users;

/// <summary>
/// 用户仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 部分见 <see cref="Common.IRepository{TEntity, TId}"/></b>：<c>GetAsync</c> /
/// <c>AddAsync</c> / <c>UpdateAsync</c> / <c>RemoveAsync</c> 由基接口统一声明，
/// 本接口只保留用户特有的查询与计数。</para>
/// <para><b>位置</b>：接口定义在 <c>Core</c>，实现在 <c>Infrastructure/Persistence</c>；
/// <c>Core</c> 不得引用 Dapper / SQLite。</para>
/// <para><b>软删除语义</b>：所有读取方法<b>默认过滤</b>已软删除的用户
/// （<c>WHERE is_deleted = 0</c>）；按主键查中软删除行按「不存在」处理（返回 <see langword="null"/>），
/// 不暴露"该行已删除"这一事实。<see cref="RemoveAsync"/> 也是软删除。</para>
/// <para><b>角色</b>：<see cref="User.RoleId"/> 指向 <c>roles</c> 表（见 <see cref="Role"/>），
/// 本仓储只负责按角色主键统计/筛选，不负责角色本身的增删。</para>
/// <para><b>审计填充</b>：<see cref="AddAsync"/> / <see cref="UpdateAsync"/> / <see cref="RemoveAsync"/>
/// 由实现负责写入创建 / 修改 / 删除审计（时间与操作人来自 <c>TimeProvider</c> 与 <c>ICurrentUser</c>），
/// 调用方无需自行调用 <c>MarkCreated</c> 等方法。</para>
/// <para><b>唯一性</b>：用户名的唯一约束由数据库（部分唯一索引 + 不区分大小写）保证，
/// <see cref="ExistsByUsernameAsync"/> 只用于提前给出友好错误，不能替代约束。</para>
/// </remarks>
public interface IUserRepository : Common.IRepository<User, Guid>
{
    /// <summary>
    /// 按用户名查询用户（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="username">用户名（调用方无需预处理大小写与首尾空白）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断用户名是否已被占用（不区分大小写，忽略已软删除的用户）。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页列出用户（按创建时间升序，其次按用户名）。
    /// </summary>
    /// <param name="skip">跳过的行数（≥ 0）。</param>
    /// <param name="take">取出的行数（&gt; 0）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户列表（可能为空）。</returns>
    Task<IReadOnlyList<User>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计未删除的用户总数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户数。</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计指定角色的用户数，用于「不得删除最后一名管理员」之类的规则判定。
    /// </summary>
    /// <param name="roleId">角色主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该角色的用户数。</returns>
    Task<int> CountByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default);



}
