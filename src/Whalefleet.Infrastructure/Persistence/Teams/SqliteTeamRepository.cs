using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Teams;

namespace Whalefleet.Infrastructure.Persistence.Teams;

/// <summary>
/// 团队仓储的 SQLite / Dapper 实现（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>显式列名</b>：与 <c>SqliteUserRepository</c> 同口径，不用 <c>SELECT *</c>。</para>
/// <para><b>软删除</b>：读取与更新都追加 <c>is_deleted = 0</c>；团队名的唯一性由部分唯一索引兜底。</para>
/// </remarks>
internal sealed class SqliteTeamRepository : RepositoryBase<Team, Guid>, ITeamRepository
{
    private const string _columns =
        "id, name, display_name, description, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM teams
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByNameSql = $"""
        SELECT {_columns}
        FROM teams
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByNameSql = $"""
        SELECT COUNT(1)
        FROM teams
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM teams
        WHERE {SoftDeleteFilter}
        ORDER BY creation_time, name
        """;

    private static readonly string _listSql = $"""
        SELECT {_columns}
        FROM teams
        WHERE {SoftDeleteFilter}
        ORDER BY creation_time, name
        LIMIT @Take OFFSET @Skip
        """;

    private static readonly string _countSql = $"""
        SELECT COUNT(1)
        FROM teams
        WHERE {SoftDeleteFilter}
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO teams ({_columns})
        VALUES (@Id, @Name, @DisplayName, @Description, @CreationTime, @CreatorId,
                @LastModificationTime, @LastModifierId, @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE teams
        SET display_name = @DisplayName,
            description = @Description,
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
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteTeamRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<Team?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<Team>(
            new CommandDefinition(_selectByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<Team?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Team>(
            new CommandDefinition(_selectByNameSql, new { Name = name.Trim() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(_existsByNameSql, new { Name = name.Trim() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Team>> ListAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var teams = await connection.QueryAsync<Team>(
            new CommandDefinition(_listSql, new { Skip = skip, Take = take }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return teams.AsList();
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(_countSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return checked((int)count);
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        Team team,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(team), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        Team team,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(team), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    private static object ToParameters(Team team) => new
    {
        Id = team.Id.ToStorageText(),
        team.Name,
        team.DisplayName,
        team.Description,
        team.CreationTime,
        CreatorId = team.CreatorId.ToStorageText(),
        team.LastModificationTime,
        LastModifierId = team.LastModifierId.ToStorageText(),
        team.IsDeleted,
        team.DeletionTime,
        DeleterId = team.DeleterId.ToStorageText(),
    };
}
