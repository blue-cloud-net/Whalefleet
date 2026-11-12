namespace Whalefleet.Application.Abstractions;

/// <summary>以完整容器 ID 计算主机上限与容器角色的交集。</summary>
public interface IContainerPermissionChecker
{
    /// <summary>根据实时数据库分配检查指定用户是否为全局管理员。</summary>
    Task<bool> IsAdministratorAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>读取容器有效权限，空集合表示不可访问。</summary>
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, Guid hostId, string containerId, CancellationToken cancellationToken = default);
    /// <summary>判断主机上的容器能力是否存在收窄。</summary>
    Task<bool> IsRestrictedAsync(Guid userId, Guid hostId, CancellationToken cancellationToken = default);
}
