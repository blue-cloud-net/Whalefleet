namespace Whalefleet.Core.Teams;

/// <summary>
/// 把某个角色<b>授予</b>某个团队，可带主机 / 容器作用域。
/// </summary>
/// <remarks>
/// <para><b>它表达的是「上限」而不是「授权」</b>：成员的有效权限 =
/// 个人权限 ∩ 本团队（及其他所属团队）授予的权限并集。详见 <see cref="Team"/>。</para>
/// <para><b>作用域规则与 <c>UserRoleAssignment</c> 一致</b>：
/// <see cref="HostId"/> 为 <see langword="null"/> 即全局；非空即该主机；
/// <see cref="ContainerId"/> 非空表示主机上的某个容器（必须同时带主机）。</para>
/// <para><b>多团队并存</b>：同一用户在多个团队里时，各团队授予的权限<b>先取并集</b>，
/// 再与个人权限取交集。并集在前是关键——取交集会把多团队协作变成「必须同时满足」，
/// 极易把人锁死。</para>
/// <para><b>软删除</b>：撤销走软删除（保留分配历史），因此撤销后可重新授予同一组合。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Assignment 是领域名词（一条授权记录），非 CA1711 所针对的保留后缀模式。")]
public sealed class TeamRoleAssignment : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 获得授权的团队主键。
    /// </summary>
    public Guid TeamId { get; private set; }

    /// <summary>
    /// 被授予的角色主键。
    /// </summary>
    public Guid RoleId { get; private set; }

    /// <summary>
    /// 作用域主机主键；<see langword="null"/> 表示<b>全局授权</b>。
    /// </summary>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// 容器完整 ID；空值表示全局或主机授权。
    /// </summary>
    public string? ContainerId { get; private set; }

    /// <summary>
    /// 是否为全局授权（<see cref="HostId"/> 与 <see cref="ContainerId"/> 均为空）。
    /// </summary>
    public bool IsGlobal => this.HostId is null && this.ContainerId is null;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Grant"/>。
    /// </summary>
    private TeamRoleAssignment()
    {
    }

    /// <summary>
    /// 建立一条团队角色授权。
    /// </summary>
    /// <param name="id">记录主键。</param>
    /// <param name="teamId">团队主键；不得为空。</param>
    /// <param name="roleId">角色主键；不得为空。</param>
    /// <param name="hostId">作用域主机主键；传 <see langword="null"/> 表示全局授权。</param>
    /// <param name="containerId">容器完整 ID；传 <see langword="null"/> 表示非容器级。</param>
    /// <returns>新建的授权记录。</returns>
    /// <exception cref="ArgumentException">团队或角色主键为空，或容器级授权缺少主机。</exception>
    public static TeamRoleAssignment Grant(
        Guid id,
        Guid teamId,
        Guid roleId,
        Guid? hostId = null,
        string? containerId = null)
    {
        if (teamId == Guid.Empty)
        {
            throw new ArgumentException("团队角色授权必须指定团队", nameof(teamId));
        }

        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("团队角色授权必须指定角色", nameof(roleId));
        }

        if (hostId == Guid.Empty)
        {
            throw new ArgumentException("主机主键不得为空", nameof(hostId));
        }

        if (containerId is not null && (hostId is null || String.IsNullOrWhiteSpace(containerId)))
        {
            throw new ArgumentException("容器授权必须指定主机与容器 ID", nameof(containerId));
        }

        return new TeamRoleAssignment
        {
            Id = id,
            TeamId = teamId,
            RoleId = roleId,
            HostId = hostId,
            ContainerId = containerId,
        };
    }

    /// <summary>
    /// 判断这条授权在指定主机上是否生效。
    /// </summary>
    /// <param name="hostId">待判定的主机主键。</param>
    /// <returns>全局授权恒返回 <see langword="true"/>；主机授权仅在主机相同时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="hostId"/> 为空。</exception>
    public bool AppliesTo(Guid hostId)
    {
        if (hostId == Guid.Empty)
        {
            throw new ArgumentException("主机主键不得为空", nameof(hostId));
        }

        return this.ContainerId is null && (this.HostId is null || this.HostId.Value == hostId);
    }
}
