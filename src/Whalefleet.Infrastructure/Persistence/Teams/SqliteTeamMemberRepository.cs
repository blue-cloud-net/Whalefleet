using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Teams;

namespace Whalefleet.Infrastructure.Persistence.Teams;

/// <summary>
/// 团队成员仓储的 SQLite / Dapper 实现（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>硬删除</b>：成员关系没有独立生命周期与审计价值，移除就是删掉这行
/// （与 <c>host_tag_assignments</c> 同口径）。</para>
/// <para><b>幂等由数据库兜住</b>：复合主键 + <c>INSERT OR IGNORE</c>，
/// 重复加同一成员不会报错、也不会产生第二行。</para>
/// </remarks>
internal sealed class SqliteTeamMemberRepository : ITeamMemberRepository
{
    private readonly IDbConnectionFactory _connections;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户（写入审计列）。</param>
    /// <param name="timeProvider">时间源（写入审计列）。</param>
    public SqliteTeamMemberRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _connections = connections;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListMemberIdsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT user_id FROM team_members WHERE team_id = @TeamId",
                new { TeamId = teamId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows
            .Select(value => Guid.Parse(value))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListTeamIdsByUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
        {
            return [];
        }

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT team_id FROM team_members WHERE user_id IN @UserIds",
                new { UserIds = userIds.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows
            .Select(value => Guid.Parse(value))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT OR IGNORE INTO team_members (team_id, user_id, creation_time, creator_id)
                VALUES (@TeamId, @UserId, @CreationTime, @CreatorId)
                """,
                new
                {
                    TeamId = teamId.ToStorageText(),
                    UserId = userId.ToStorageText(),
                    CreationTime = _timeProvider.GetUtcNow().UtcDateTime,
                    CreatorId = (_currentUser.UserId ?? Guid.Empty).ToStorageText(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM team_members WHERE team_id = @TeamId AND user_id = @UserId",
                new { TeamId = teamId.ToStorageText(), UserId = userId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COUNT(1) FROM team_members WHERE team_id = @TeamId AND user_id = @UserId",
                new { TeamId = teamId.ToStorageText(), UserId = userId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountMembersAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(teamIds);

        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<MemberCountRow>(
            new CommandDefinition(
                """
                SELECT team_id AS TeamId, COUNT(1) AS Count
                FROM team_members
                WHERE team_id IN @TeamIds
                GROUP BY team_id
                """,
                new { TeamIds = teamIds.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.ToDictionary(row => Guid.Parse(row.TeamId), row => row.Count);
    }

    /// <summary>
    /// 成员计数查询的行投影。
    /// </summary>
    /// <remarks>
    /// 单独定义而不是用 tuple：Dapper 的物化对 record 构造函数与公开属性都支持，
    /// 但项目内既有写法统一是「内部类 + 公开属性」，这里保持一致。
    /// </remarks>
    private sealed class MemberCountRow
    {
        /// <summary>团队主键（小写 36 位文本）。</summary>
        public string TeamId { get; set; } = string.Empty;

        /// <summary>成员数。</summary>
        public int Count { get; set; }
    }
}
