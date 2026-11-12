using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Infrastructure.Persistence;

namespace Whalefleet.Infrastructure.Permissions;

/// <summary>以单事务更新重建后的授权目标。</summary>
internal sealed class SqliteContainerAssignmentStore(IDbConnectionFactory connections) : IContainerAssignmentStore
{
    /// <inheritdoc />
    public async Task MoveAsync(Guid hostId, string oldId, string newId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_role_assignments SET container_id = @NewId WHERE host_id = @HostId AND container_id = @OldId AND is_deleted = 0",
            new { HostId = hostId.ToStorageText(), OldId = oldId, NewId = newId }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
