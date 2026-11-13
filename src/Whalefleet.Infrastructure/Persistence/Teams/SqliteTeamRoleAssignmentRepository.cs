using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Teams;

namespace Whalefleet.Infrastructure.Persistence.Teams;

/// <summary>
/// 团队角色授权仓储的 SQLite / Dapper 实现（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>软删除</b>：撤销走软删除（保留授权历史），因此撤销后可重新授予同一组合。
/// 部分唯一索引 <c>(team_id, role_id, COALESCE(host_id,''), COALESCE(container_id,''))</c>
/// 兜住重复授予。</para>
/// </remarks>
internal sealed class SqliteTeamRoleAssignmentRepository
    : RepositoryBase<TeamRoleAssignment, Guid>, ITeamRoleAssignmentRepository
{
    private const string _columns =
        "id, team_id, role_id, host_id, container_id, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM team_role_assignments
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM team_role_assignments
        WHERE {SoftDeleteFilter}
        ORDER BY creation_time
        """;

    private static readonly string _listByTeamSql = $"""
        SELECT {_columns}
        FROM team_role_assignments
        WHERE team_id = @TeamId AND {SoftDeleteFilter}
        ORDER BY creation_time
        """;

    private static readonly string _listByTeamsSql = $"""
        SELECT {_columns}
        FROM team_role_assignments
        WHERE team_id IN @TeamIds AND {SoftDeleteFilter}
        ORDER BY creation_time
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO team_role_assignments ({_columns})
        VALUES (@Id, @TeamId, @RoleId, @HostId, @ContainerId, @CreationTime, @CreatorId,
                @LastModificationTime, @LastModifierId, @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE team_role_assignments
        SET last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId,
            is_deleted = @IsDeleted,
            deletion_time = @DeletionTime,
            deleter_id = @DeleterId
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteTeamRoleAssignmentRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<TeamRoleAssignment?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<TeamRoleAssignment>(
            new CommandDefinition(_selectByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamRoleAssignment>> ListByTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<TeamRoleAssignment>(
            new CommandDefinition(
                _listByTeamSql,
                new { TeamId = teamId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamRoleAssignment>> ListByTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(teamIds);

        if (teamIds.Count == 0)
        {
            return [];
        }

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<TeamRoleAssignment>(
            new CommandDefinition(
                _listByTeamsSql,
                new { TeamIds = teamIds.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<TeamRoleAssignment?> GetAsync(
        Guid teamId,
        Guid roleId,
        Guid? hostId,
        string? containerId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<TeamRoleAssignment>(
            new CommandDefinition(
                """
                SELECT id, team_id, role_id, host_id, container_id, creation_time, creator_id,
                       last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id
                FROM team_role_assignments
                WHERE team_id = @TeamId AND role_id = @RoleId
                  AND COALESCE(host_id, '') = @HostId
                  AND COALESCE(container_id, '') = @ContainerId
                  AND is_deleted = 0
                """,
                new
                {
                    TeamId = teamId.ToStorageText(),
                    RoleId = roleId.ToStorageText(),
                    HostId = (hostId ?? Guid.Empty).ToStorageText(),
                    ContainerId = containerId ?? String.Empty,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        TeamRoleAssignment assignment,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(assignment), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        TeamRoleAssignment assignment,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(assignment), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    private static object ToParameters(TeamRoleAssignment assignment) => new
    {
        Id = assignment.Id.ToStorageText(),
        TeamId = assignment.TeamId.ToStorageText(),
        RoleId = assignment.RoleId.ToStorageText(),
        HostId = assignment.HostId?.ToStorageText(),
        assignment.ContainerId,
        assignment.CreationTime,
        CreatorId = assignment.CreatorId.ToStorageText(),
        assignment.LastModificationTime,
        LastModifierId = assignment.LastModifierId.ToStorageText(),
        assignment.IsDeleted,
        assignment.DeletionTime,
        DeleterId = assignment.DeleterId.ToStorageText(),
    };
}
