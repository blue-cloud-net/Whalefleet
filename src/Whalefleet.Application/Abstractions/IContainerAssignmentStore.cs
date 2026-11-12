namespace Whalefleet.Application.Abstractions;

/// <summary>平台重建成功后的授权迁移。</summary>
public interface IContainerAssignmentStore
{
    /// <summary>事务迁移同一主机上旧容器的全部有效分配。</summary>
    Task MoveAsync(Guid hostId, string oldId, string newId, CancellationToken cancellationToken = default);
}
