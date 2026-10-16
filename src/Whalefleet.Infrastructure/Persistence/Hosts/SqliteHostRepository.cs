using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Infrastructure.Persistence.Hosts;

/// <summary>
/// Docker 主机仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>凭据的两列如何落到一个属性</b>：<c>DockerHost</c> 对外只暴露
/// <c>Credentials</c>（值对象），但库里是 <c>auth_kind</c> + <c>auth_config_json</c> 两列。
/// 实体内部用两个 <c>internal</c> 投影属性接收 Dapper 物化结果（Dapper 靠
/// <c>skipVisibility</c> 可写非公开 setter），因此仓储读写都<b>通过 <c>Credentials</c></b>，
/// 不需要 <c>InternalsVisibleTo</c>，也不会把凭据载荷提升为公开可写属性。</para>
/// <para><b>端点不做规范化</b>：库里存原文，客户端缓存的键由 <c>DockerEndpointNormalizer</c>
/// 在取连接时现算，所以两种写法指向同一守护进程时仍会共享客户端。</para>
/// </remarks>
internal sealed class SqliteHostRepository : RepositoryBase<DockerHost, Guid>, IHostRepository
{
    private const string _columns =
        "id, name, type, endpoint, auth_kind, auth_config_json, enabled, group_id, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM hosts
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByNameSql = $"""
        SELECT {_columns}
        FROM hosts
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByNameSql = $"""
        SELECT COUNT(1)
        FROM hosts
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByIdsSql = $"""
        SELECT {_columns}
        FROM hosts
        WHERE id IN @Ids AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM hosts
        WHERE {SoftDeleteFilter}
        ORDER BY creation_time, name
        """;

    private static readonly string _countSql = $"""
        SELECT COUNT(1)
        FROM hosts
        WHERE {SoftDeleteFilter}
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO hosts ({_columns})
        VALUES (@Id, @Name, @Type, @Endpoint, @AuthKind, @AuthConfigJson, @Enabled, @GroupId,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE hosts
        SET name = @Name,
            type = @Type,
            endpoint = @Endpoint,
            auth_kind = @AuthKind,
            auth_config_json = @AuthConfigJson,
            enabled = @Enabled,
            group_id = @GroupId,
            last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId,
            is_deleted = @IsDeleted,
            deletion_time = @DeletionTime,
            deleter_id = @DeleterId
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _queryByGroupSql = $"""
        SELECT {_columns}
        FROM hosts
        WHERE group_id = @GroupId AND {SoftDeleteFilter}
        ORDER BY creation_time, name
        """;

    private static readonly string _countByGroupSql = $"""
        SELECT group_id, COUNT(1)
        FROM hosts
        WHERE group_id IS NOT NULL AND {SoftDeleteFilter}
        GROUP BY group_id
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteHostRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<DockerHost?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<DockerHost>(
            new CommandDefinition(_selectByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<DockerHost?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<DockerHost>(
            new CommandDefinition(_selectByNameSql, new { Name = name.Trim() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DockerHost>> QueryListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DockerHost>(
            new CommandDefinition(
                _selectByIdsSql,
                new { Ids = ids.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(_existsByNameSql, new { Name = name.Trim() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(_countSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return checked((int)count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountByGroupAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<(string GroupId, long Count)>(
            new CommandDefinition(_countByGroupSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // 读取侧显式解析：库里存的是小写 36 位文本（写入侧统一走 ToStorageText），
        // 直接让 Dapper 映 Guid 会依赖类型处理器对文本宽容度，这里明确一点
        return rows.ToDictionary(
            row => Guid.Parse(row.GroupId),
            row => checked((int)row.Count));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DockerHost>> QueryByGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DockerHost>(
            new CommandDefinition(
                _queryByGroupSql,
                new { GroupId = groupId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows];
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        DockerHost host,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(host), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        DockerHost host,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(host), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />


    private static object ToParameters(DockerHost host) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器（值类型直接装箱），
        // 不转的话主键会按驱动默认的大写形态落库、枚举会落成 0/1。
        Id = host.Id.ToStorageText(),
        host.Name,
        Type = host.Type.ToStorageText(),
        AuthKind = host.Credentials.Kind.ToStorageText(),
        Endpoint = host.Endpoint.Value,
        AuthConfigJson = host.Credentials.ConfigJson,
        host.Enabled,
        // 可空外键：未分组时必须写 NULL 而不是空串，否则外键约束会把空串当成"指向不存在的主键"
        GroupId = host.GroupId?.ToStorageText(),
        host.CreationTime,
        CreatorId = host.CreatorId.ToStorageText(),
        host.LastModificationTime,
        LastModifierId = host.LastModifierId.ToStorageText(),
        host.IsDeleted,
        host.DeletionTime,
        DeleterId = host.DeleterId.ToStorageText(),
    };
}
