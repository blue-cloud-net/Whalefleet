namespace Whalefleet.Core.Permissions;

/// <summary>
/// 用户对某个容器的授权（主机粒度授权的细粒度补充）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须带 HostId</b>：容器 ID 只在单台 Docker 守护进程内唯一，
/// 不同主机上完全可能出现同名 / 同 ID 的容器；不带 <c>HostId</c> 的容器授权记录会串权。</para>
/// <para><b>唯一性</b>：<c>(user_id, host_id, container_id) WHERE is_deleted = 0</c>。</para>
/// <para><b>落地阶段</b>：0.3.0（本迭代只定义实体与接口，不建表）。</para>
/// <para><b>命名抑制</b>：<c>CA1711</c> 因类型名以 <c>Permission</c> 结尾而报警，
/// 但本类型就是「一条授权」这一领域概念，改名反而失真，故就地精确抑制。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Permission 是领域名词（一条授权记录），非 CA1711 所针对的保留后缀模式。")]
public sealed class UserContainerPermission : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 容器标识最大长度（Docker 容器 ID / 名称）。
    /// </summary>
    public const int MaxContainerIdLength = 128;

    /// <summary>
    /// 被授权的用户主键。
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 容器所属主机的主键。
    /// </summary>
    public Guid HostId { get; private set; }

    /// <summary>
    /// 容器标识（Docker 容器 ID 或名称），去首尾空白后保存。
    /// </summary>
    public string ContainerId { get; private set; } = default!;

    /// <summary>
    /// 权限位组合。
    /// </summary>
    public ContainerPermissions Permissions { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Grant"/>。
    /// </summary>
    private UserContainerPermission()
    {
    }

    /// <summary>
    /// 建立一条容器授权。
    /// </summary>
    /// <param name="id">记录主键。</param>
    /// <param name="userId">用户主键；不得为空。</param>
    /// <param name="hostId">主机主键；不得为空。</param>
    /// <param name="containerId">容器标识；不得为空。</param>
    /// <param name="permissions">权限位组合；不得含未定义的位。</param>
    /// <returns>新建的授权记录。</returns>
    /// <exception cref="ArgumentException">主键 / 容器标识为空，或权限位含未定义的值。</exception>
    public static UserContainerPermission Grant(
        Guid id,
        Guid userId,
        Guid hostId,
        string containerId,
        ContainerPermissions permissions)
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

        return new UserContainerPermission
        {
            Id = id,
            UserId = userId,
            HostId = hostId,
            ContainerId = NormalizeContainerId(containerId),
            Permissions = permissions,
        };
    }

    /// <summary>
    /// 覆盖式更新权限位。
    /// </summary>
    /// <param name="permissions">新的权限位组合。</param>
    /// <exception cref="ArgumentException">权限位含未定义的值。</exception>
    public void UpdatePermissions(ContainerPermissions permissions)
    {
        EnsureKnownPermissions(permissions);
        this.Permissions = permissions;
    }

    /// <summary>
    /// 判断是否具备指定的权限位（支持组合位：须<b>全部</b>具备，无隐式包含）。
    /// </summary>
    /// <param name="permission">待判定的权限位。</param>
    /// <returns>具备返回 <see langword="true"/>；<see cref="ContainerPermissions.None"/> 恒为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException">权限位含未定义的值。</exception>
    public bool Has(ContainerPermissions permission)
    {
        EnsureKnownPermissions(permission);
        return permission == ContainerPermissions.None || (this.Permissions & permission) == permission;
    }

    private static string NormalizeContainerId(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var trimmed = containerId.Trim();
        if (trimmed.Length > MaxContainerIdLength)
        {
            throw new ArgumentException(
                $"容器标识长度不得超过 {MaxContainerIdLength} 个字符",
                nameof(containerId));
        }

        return trimmed;
    }

    private static void EnsureKnownPermissions(ContainerPermissions permissions)
    {
        if ((permissions & ~ContainerPermissions.All) != 0)
        {
            throw new ArgumentException($"未知的权限位：{(int)permissions}", nameof(permissions));
        }
    }
}
