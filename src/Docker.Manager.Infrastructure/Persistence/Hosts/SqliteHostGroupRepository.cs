using Dapper;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Infrastructure.Persistence.Hosts;

/// <summary>
/// 主机分组仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>排序</b>：全量列表按 <c>sort_order</c> 升序、同权重按名称——
/// 前端不再重排。若让前端排，「刷新之后分组换了位置」会让人怀疑数据被动过。</para>
/// <para><b>为什么不在这里做「删除分组时清空组内主机」</b>：那是跨聚合的操作，
/// 且需要写主机实体的审计字段。写成一条批量 UPDATE 会绕过审计填充，
/// 由 <c>HostGroupService</c> 逐台更新（见 <see cref="IHostRepository.QueryByGroupAsync"/> 的说明）。</para>
/// </remarks>
internal sealed class SqliteHostGroupRepository : RepositoryBase<HostGroup, Guid>, IHostGroupRepository
{
    private const string _columns =
        "id, name, description, sort_order, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM host_groups
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByNameSql = $"""
        SELECT {_columns}
        FROM host_groups
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByNameSql = $"""
        SELECT COUNT(1)
        FROM host_groups
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM host_groups
        WHERE {SoftDeleteFilter}
        ORDER BY sort_order, name
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO host_groups ({_columns})
        VALUES (@Id, @Name, @Description, @SortOrder, @CreationTime, @CreatorId,
                @LastModificationTime, @LastModifierId, @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE host_groups
        SET name = @Name,
            description = @Description,
            sort_order = @SortOrder,
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
    public SqliteHostGroupRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<HostGroup?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<HostGroup>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<HostGroup?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<HostGroup>(
            new CommandDefinition(
                _selectByNameSql,
                new { Name = name.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
            new CommandDefinition(
                _existsByNameSql,
                new { Name = name.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        HostGroup group,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(group), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        HostGroup group,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(group), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    private static object ToParameters(HostGroup group) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器，主键不转会按驱动默认的大写形态落库
        Id = group.Id.ToStorageText(),
        group.Name,
        group.Description,
        group.SortOrder,
        group.CreationTime,
        CreatorId = group.CreatorId.ToStorageText(),
        group.LastModificationTime,
        LastModifierId = group.LastModifierId.ToStorageText(),
        group.IsDeleted,
        group.DeletionTime,
        DeleterId = group.DeleterId.ToStorageText(),
    };
}
