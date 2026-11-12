using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;

namespace Whalefleet.Infrastructure.Permissions;

/// <summary>持久化实例级授权模式，不跨请求缓存。</summary>
internal sealed class SqliteAuthorizationSettingsStore(IDbConnectionFactory connections) : IAuthorizationSettingsStore
{
    /// <inheritdoc />
    public async Task<string> GetModeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT container_permission_mode FROM authorization_settings WHERE id = 1",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(mode is "inheritHost" or "requireAssignment", true);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE authorization_settings SET container_permission_mode = @Mode WHERE id = 1",
            new { Mode = mode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
