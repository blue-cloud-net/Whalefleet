namespace Docker.Manager.Core.Permissions;

/// <summary>
/// 用户对某台主机的授权。
/// </summary>
/// <remarks>
/// <para><b>唯一性</b>：一个用户对一台主机只应有一条有效记录（数据库部分唯一索引
/// <c>(user_id, host_id) WHERE is_deleted = 0</c>）；改权限走
/// <see cref="UpdatePermissions"/>，不是再插一条。</para>
/// <para><b>撤销</b>：软删除（继承 <see cref="FullAuditedEntity{TId, TUserKey}"/>），
/// 保留授权历史；撤销后可重新授权（唯一索引只看未删除行）。</para>
/// <para><b>落地阶段</b>：0.3.0（本迭代只定义实体与接口，不建表）。</para>
/// <para><b>命名抑制</b>：<c>CA1711</c> 因类型名以 <c>Permission</c> 结尾而报警，
/// 但本类型就是「一条授权」这一领域概念，改名反而失真，故就地精确抑制。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Permission 是领域名词（一条授权记录），非 CA1711 所针对的保留后缀模式。")]
public sealed class UserHostPermission : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 被授权的用户主键。
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 被授权的主机主键。
    /// </summary>
    public Guid HostId { get; private set; }

    /// <summary>
    /// 权限位组合。
    /// </summary>
    public HostPermissions Permissions { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Grant"/>。
    /// </summary>
    private UserHostPermission()
    {
    }

    /// <summary>
    /// 建立一条主机授权。
    /// </summary>
    /// <param name="id">记录主键。</param>
    /// <param name="userId">用户主键；不得为空。</param>
    /// <param name="hostId">主机主键；不得为空。</param>
    /// <param name="permissions">权限位组合；不得含未定义的位。</param>
    /// <returns>新建的授权记录。</returns>
    /// <exception cref="ArgumentException">主键为空，或权限位含未定义的值。</exception>
    public static UserHostPermission Grant(Guid id, Guid userId, Guid hostId, HostPermissions permissions)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("授权必须指定用户", nameof(userId));
        }

        if (hostId == Guid.Empty)
        {
            throw new ArgumentException("授权必须指定主机", nameof(hostId));
        }

        EnsureKnownPermissions(permissions);

        return new UserHostPermission
        {
            Id = id,
            UserId = userId,
            HostId = hostId,
            Permissions = permissions,
        };
    }

    /// <summary>
    /// 覆盖式更新权限位。
    /// </summary>
    /// <param name="permissions">新的权限位组合。</param>
    /// <exception cref="ArgumentException">权限位含未定义的值。</exception>
    public void UpdatePermissions(HostPermissions permissions)
    {
        EnsureKnownPermissions(permissions);
        this.Permissions = permissions;
    }

    /// <summary>
    /// 判断是否具备指定的权限位（支持组合位：须<b>全部</b>具备，无隐式包含）。
    /// </summary>
    /// <param name="permission">待判定的权限位。</param>
    /// <returns>具备返回 <see langword="true"/>；<see cref="HostPermissions.None"/> 恒为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException">权限位含未定义的值。</exception>
    public bool Has(HostPermissions permission)
    {
        EnsureKnownPermissions(permission);
        return permission == HostPermissions.None || (this.Permissions & permission) == permission;
    }

    private static void EnsureKnownPermissions(HostPermissions permissions)
    {
        if ((permissions & ~HostPermissions.All) != 0)
        {
            throw new ArgumentException($"未知的权限位：{(int)permissions}", nameof(permissions));
        }
    }
}
