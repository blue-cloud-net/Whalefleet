namespace Whalefleet.Core.Permissions;

/// <summary>
/// 把某个角色**分配**给某个用户，可带主机作用域。
/// </summary>
/// <remarks>
/// <para><b>为什么叫「分配」而不是「用户角色」</b>：这条记录同时表达「谁」与「在哪」。
/// 叫 <c>UserRole</c> 会让它看起来像一张纯多对多连接表，而它真正携带的是**生效范围**这一维。</para>
/// <para><b><see cref="HostId"/> 为 <see langword="null"/> 即全局分配</b>：
/// 此时角色的 Global 权限生效，Host 域权限则对<b>所有</b>主机生效。
/// <see cref="HostId"/> 非空时，只在该主机上生效，<b>且该角色的 Global 权限被忽略</b>——
/// 否则「把 admin 分配到某台主机」就成了系统级提权。</para>
/// <para><b>多角色并存</b>：同一用户在同一主机上可持有多条分配，有效权限取并集。
/// 唯一索引是 <c>(user_id, role_id, host_id) WHERE is_deleted = 0</c>。</para>
/// <para><b>⚠️ <see cref="HostId"/> 可空带来的实现约束</b>：SQLite 的唯一索引<b>不去重 NULL</b>，
/// 因此「同一全局角色重复分配」在纯唯一索引下拦不住。建表须用哨兵值（空串）或
/// 表达式索引（<c>COALESCE(host_id, '')</c>）兜住，见迁移 <c>0006</c>。</para>
/// <para><b>软删除</b>：撤销走软删除（保留分配历史），因此撤销后可重新分配同一组合。</para>
/// <para><b>命名抑制</b>：<c>CA1711</c> 因类型名以 <c>Assignment</c> 结尾不报警；
/// 但 <see cref="UserRoleAssignment"/> 的尾词是「授权记录」，沿用既有
/// <c>UserHostPermission</c> 的就地抑制风格。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Permission 是领域名词（一条授权记录），非 CA1711 所针对的保留后缀模式。")]
public sealed class UserRoleAssignment : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 被分配的用户主键。
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 被分配的角色主键。
    /// </summary>
    public Guid RoleId { get; private set; }

    /// <summary>
    /// 作用域主机主键；<see langword="null"/> 表示**全局分配**。
    /// </summary>
    /// <remarks>
    /// 不变量由数据库外键与部分唯一索引兜底；实体只拒绝<b>空字符串</b>这种
    /// 「本想表达 null 却写错了」的输入。
    /// </remarks>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// 是否为全局分配（<see cref="HostId"/> 为 <see langword="null"/>）。
    /// </summary>
    public bool IsGlobal => this.HostId is null;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Assign"/>。
    /// </summary>
    private UserRoleAssignment()
    {
    }

    /// <summary>
    /// 建立一条角色分配。
    /// </summary>
    /// <param name="id">记录主键。</param>
    /// <param name="userId">用户主键；不得为空。</param>
    /// <param name="roleId">角色主键；不得为空。</param>
    /// <param name="hostId">作用域主机主键；传 <see langword="null"/> 表示全局分配。</param>
    /// <returns>新建的分配记录。</returns>
    /// <exception cref="ArgumentException">用户或角色主键为空。</exception>
    public static UserRoleAssignment Assign(Guid id, Guid userId, Guid roleId, Guid? hostId = null)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("角色分配必须指定用户", nameof(userId));
        }

        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("角色分配必须指定角色", nameof(roleId));
        }

        return new UserRoleAssignment
        {
            Id = id,
            UserId = userId,
            RoleId = roleId,
            HostId = hostId,
        };
    }

    /// <summary>
    /// 判断这条分配在指定主机上是否生效。
    /// </summary>
    /// <param name="hostId">待判定的主机主键。</param>
    /// <returns>全局分配恒返回 <see langword="true"/>；主机分配仅在主机相同时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="hostId"/> 为空。</exception>
    /// <remarks>
    /// 这个判定<b>只回答「这条分配在不在生效范围内」</b>，不回答「它带不带某项权限」——
    /// 后者要把角色集合并起来算，交给应用层。单条分配自己判断权限会漏掉「另一个角色也带这个权限」
    /// 这种并集情形。
    /// </remarks>
    public bool AppliesTo(Guid hostId)
    {
        if (hostId == Guid.Empty)
        {
            throw new ArgumentException("主机主键不得为空", nameof(hostId));
        }

        return this.HostId is null || this.HostId.Value == hostId;
    }
}
