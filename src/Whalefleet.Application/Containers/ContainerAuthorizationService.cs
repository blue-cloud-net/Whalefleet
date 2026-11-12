using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Application.Containers;

/// <summary>容器操作的统一授权入口，标识解析后再计算权限。</summary>
public sealed class ContainerAuthorizationService(
    IDockerHostAccessor accessor,
    ICurrentUser currentUser,
    IPermissionChecker hostPermissions,
    IContainerPermissionChecker permissions)
{
    /// <summary>取得已解析容器的权限，不进行额外 inspect。</summary>
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(string hostName, string containerId, CancellationToken cancellationToken = default)
    {
        var host = await accessor.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);
        return await permissions.GetPermissionsAsync(currentUser.UserId ?? Guid.Empty, host.Id, containerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验容器操作权限，并返回完整 ID。</summary>
    public async Task<string> EnsureAsync(string hostName, string containerId, string permission, CancellationToken cancellationToken = default) =>
        await this.EnsureForUserAsync(currentUser.UserId ?? Guid.Empty, hostName, containerId, permission, cancellationToken).ConfigureAwait(false);

    /// <summary>校验显式用户的容器权限，适用于终端与实时通道。</summary>
    public async Task<string> EnsureForUserAsync(Guid userId, string hostName, string containerId, string permission, CancellationToken cancellationToken = default)
    {
        var host = await accessor.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);
        var user = userId;
        await hostPermissions.EnsurePermissionAsync(user, host.Id, permission, cancellationToken).ConfigureAwait(false);
        if (await permissions.IsAdministratorAsync(user, cancellationToken).ConfigureAwait(false)) return containerId;
        var client = await accessor.GetClientAsync(hostName, cancellationToken).ConfigureAwait(false);
        var container = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken), "容器授权", cancellationToken).ConfigureAwait(false);
        var effective = await permissions.GetPermissionsAsync(user, host.Id, container.ID, cancellationToken).ConfigureAwait(false);
        if (!effective.Contains(permission)) throw new BusinessException(ErrorCodes.OperationForbidden, "无权访问或操作该容器");
        return container.ID;
    }

    /// <summary>关联资源写入前检查全部受影响容器。</summary>
    public async Task EnsureRelatedAsync(string hostName, string kind, string resourceId, CancellationToken cancellationToken = default)
    {
        var client = await accessor.GetClientAsync(hostName, cancellationToken).ConfigureAwait(false);
        var containers = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ListContainersAsync(new global::Docker.DotNet.Models.ContainersListParameters { All = true }, cancellationToken),
            "关联容器授权", cancellationToken).ConfigureAwait(false);
        var canonicalId = resourceId;
        if (kind == "image")
        {
            canonicalId = (await DockerErrorTranslator.ExecuteAsync(
                () => client.Images.InspectImageAsync(resourceId, cancellationToken), "关联镜像授权", cancellationToken).ConfigureAwait(false)).ID;
        }
        foreach (var container in containers)
        {
            var matches = kind switch
            {
                "image" => container.Image == resourceId || container.ImageID == canonicalId,
                "volume" => container.Mounts?.Any(m => m.Name == resourceId) == true,
                "network" => container.NetworkSettings?.Networks?.Any(n => n.Key == resourceId || n.Value.NetworkID == resourceId) == true,
                _ => false,
            };
            if (matches) await this.EnsureAsync(hostName, container.ID, Permission.ContainerWrite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>拒绝可能突破容器限制的主机范围写操作。</summary>
    public Task EnsureUnrestrictedAsync(string hostName, CancellationToken cancellationToken = default) =>
        this.EnsureUnrestrictedAsync(hostName, Permission.ContainerWrite, cancellationToken);

    /// <summary>检查主机范围能力与容器收窄状态。</summary>
    public async Task EnsureUnrestrictedAsync(string hostName, string permission, CancellationToken cancellationToken = default)
    {
        var host = await accessor.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);
        await hostPermissions.EnsurePermissionAsync(currentUser.UserId ?? Guid.Empty, host.Id, permission, cancellationToken).ConfigureAwait(false);
        if (await permissions.IsRestrictedAsync(currentUser.UserId ?? Guid.Empty, host.Id, cancellationToken).ConfigureAwait(false))
            throw new BusinessException(ErrorCodes.OperationForbidden, "容器权限受限，不能执行主机范围操作");
    }
}
