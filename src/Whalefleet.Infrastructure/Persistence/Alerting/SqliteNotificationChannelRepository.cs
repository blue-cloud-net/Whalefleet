using System.Data.Common;

using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Persistence.Alerting;

/// <summary>
/// 通知渠道仓储的 SQLite / Dapper 实现。
/// </summary>
internal sealed class SqliteNotificationChannelRepository
    : RepositoryBase<NotificationChannel, Guid>, INotificationChannelRepository
{
    private const string _columns =
        "id, name, kind, enabled, config_json, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM notification_channels
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM notification_channels
        WHERE {SoftDeleteFilter}
        ORDER BY name COLLATE NOCASE
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO notification_channels ({_columns})
        VALUES (@Id, @Name, @Kind, @Enabled, @ConfigJson,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE notification_channels
        SET name = @Name,
            kind = @Kind,
            enabled = @Enabled,
            config_json = @ConfigJson,
            last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId,
            is_deleted = @IsDeleted,
            deletion_time = @DeletionTime,
            deleter_id = @DeleterId
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    public SqliteNotificationChannelRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<NotificationChannel?> QueryByIdAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<NotificationChannel>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(
        string name,
        Guid? excludeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var sql = $"""
            SELECT COUNT(1)
            FROM notification_channels
            WHERE {SoftDeleteFilter}
              AND name = @Name COLLATE NOCASE
              AND (@ExcludeId IS NULL OR id <> @ExcludeId)
            """;

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { Name = name.Trim(), ExcludeId = excludeId?.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationChannel>> QueryByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT {_columns}
            FROM notification_channels
            WHERE {SoftDeleteFilter} AND id IN @Ids
            ORDER BY name COLLATE NOCASE
            """;

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var channels = await connection.QueryAsync<NotificationChannel>(
            new CommandDefinition(
                sql,
                new { Ids = ids.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. channels];
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        DbConnection connection,
        NotificationChannel channel,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(channel), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        DbConnection connection,
        NotificationChannel channel,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(channel), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把实体转换为 Dapper 参数。
    /// </summary>
    private static object ToParameters(NotificationChannel channel) => new
    {
        Id = channel.Id.ToStorageText(),
        channel.Name,
        Kind = channel.Kind.ToStorageText(),
        channel.Enabled,
        channel.ConfigJson,
        channel.CreationTime,
        CreatorId = channel.CreatorId.ToStorageText(),
        channel.LastModificationTime,
        LastModifierId = channel.LastModifierId.ToStorageText(),
        channel.IsDeleted,
        channel.DeletionTime,
        DeleterId = channel.DeleterId.ToStorageText(),
    };
}
