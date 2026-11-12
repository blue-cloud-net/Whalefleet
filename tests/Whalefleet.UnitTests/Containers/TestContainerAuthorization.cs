using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Containers;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;

namespace Whalefleet.UnitTests.Containers;

internal sealed class TestContainerAuthorization : IContainerPermissionChecker, IPermissionChecker, ICurrentUser, IContainerAssignmentStore
{
    private static readonly IReadOnlySet<string> _permissions = new HashSet<string>([Permission.ContainerRead, Permission.ContainerWrite]);
    public Task MoveAsync(Guid hostId, string oldId, string newId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> IsAdministratorAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public bool IsAuthenticated => true;
    public Guid? UserId => Guid.Parse("10000000-0000-0000-0000-000000000001");
    public string? Username => "test";
    public IReadOnlyList<string> Roles => [];
    public static ContainerAuthorizationService Create(IDockerHostAccessor accessor)
    {
        var stub = new TestContainerAuthorization();
        return new ContainerAuthorizationService(accessor, stub, stub, stub);
    }
    public Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, Guid hostId, string containerId, CancellationToken cancellationToken = default) => Task.FromResult(_permissions);
    public Task<bool> IsRestrictedAsync(Guid userId, Guid hostId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task EnsurePermissionAsync(Guid userId, Guid? hostId, string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> HasPermissionAsync(Guid userId, Guid? hostId, string permission, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(Guid userId, Guid? hostId, CancellationToken cancellationToken = default) => Task.FromResult(_permissions);
    public Task<IReadOnlyList<DockerHost>> FilterVisibleHostsAsync(Guid userId, IReadOnlyList<DockerHost> hosts, CancellationToken cancellationToken = default) => Task.FromResult(hosts);
    public Task<bool> IsAdministratorAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
