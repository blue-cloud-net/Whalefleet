using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Infrastructure.Persistence.Hosts;

/// <summary>
/// 主机标签仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>排序</b>：全量列表按名称升序（不区分大小写）。标签没有排序权重——
/// 它是自由打标的集合，人为排序只会让「新建的标签跑到最后」变成一件需要解释的事。</para>
/// <para><b>关联查询一次完成</b>：<see cref="QueryByHostIdsAsync"/> 用一条
/// <c>JOIN</c> 把「主机 → 标签」全部取回，再在内存里分组。主机列表每次刷新都会调它，
/// 逐台查会变成 N 次往返。</para>
/// <para><b>幂等靠 <c>INSERT OR IGNORE</c> / <c>DELETE</c></b>：打标与清标都天然幂等，
/// 交给数据库判定比「先查再写」少一次往返，也没有并发窗口。</para>
/// </remarks>
internal sealed class SqliteHostTagRepository : RepositoryBase<HostTag, Guid>, IHostTagRepository
{
    private const string _columns =
        "id, name, description, color, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM host_tags
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByNameSql = $"""
        SELECT {_columns}
        FROM host_tags
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByNameSql = $"""
        SELECT COUNT(1)
        FROM host_tags
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM host_tags
        WHERE {SoftDeleteFilter}
        ORDER BY name COLLATE NOCASE
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO host_tags ({_columns})
        VALUES (@Id, @Name, @Description, @Color, @CreationTime, @CreatorId,
                @LastModificationTime, @LastModifierId, @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE host_tags
        SET name = @Name,
            description = @Description,
            color = @Color,
            last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId,
            is_deleted = @IsDeleted,
            deletion_time = @DeletionTime,
            deleter_id = @DeleterId
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    // IN 展开由 Dapper 负责；两段都带软删除过滤，避免给已删标签继续打标。
    // 列顺序即多映射的分界线：先是分组键 host_id，再是标签实体本身的全部列（splitOn: "Id"）
    private static readonly string _selectByHostIdsSql = $"""
        SELECT a.host_id, t.id, t.name, t.description, t.color, t.creation_time, t.creator_id,
               t.last_modification_time, t.last_modifier_id, t.is_deleted, t.deletion_time, t.deleter_id
        FROM host_tag_assignments a
        JOIN host_tags t ON t.id = a.tag_id
        WHERE a.host_id IN @HostIds AND t.is_deleted = 0
        ORDER BY t.name COLLATE NOCASE
        """;

    private static readonly string _countByTagSql = """
        SELECT a.tag_id AS TagId, COUNT(1) AS HostCount
        FROM host_tag_assignments a
        JOIN host_tags t ON t.id = a.tag_id
        WHERE t.is_deleted = 0
        GROUP BY a.tag_id
        """;

    private static readonly string _insertAssignmentSql = """
        INSERT OR IGNORE INTO host_tag_assignments (host_id, tag_id, creation_time, creator_id)
        VALUES (@HostId, @TagId, @CreationTime, @CreatorId)
        """;

    private static readonly string _deleteAssignmentSql = """
        DELETE FROM host_tag_assignments
        WHERE host_id IN @HostIds AND tag_id IN @TagIds
        """;

    private static readonly string _deleteAssignmentsByTagSql = """
        DELETE FROM host_tag_assignments
        WHERE tag_id = @TagId
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteHostTagRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<HostTag?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<HostTag>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<HostTag?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<HostTag>(
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
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<HostTag>>> QueryByHostIdsAsync(
        IReadOnlyCollection<Guid> hostIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostIds);

        if (hostIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<HostTag>>();
        }

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<Guid, HostTag, (Guid HostId, HostTag Tag)>(
            new CommandDefinition(
                _selectByHostIdsSql,
                new { HostIds = hostIds.Select(static id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken),
            static (hostId, tag) => (hostId, tag),
            splitOn: "id")
            .ConfigureAwait(false);

        return rows
            .GroupBy(static row => row.HostId, static row => row.Tag)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<HostTag>)[.. group]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountByTagAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<TagCountRow>(
            new CommandDefinition(_countByTagSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.ToDictionary(static row => row.TagId, static row => row.HostCount);
    }

    /// <inheritdoc />
    public async Task AssignAsync(
        IReadOnlyCollection<Guid> hostIds,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostIds);
        ArgumentNullException.ThrowIfNull(tagIds);

        if (hostIds.Count == 0 || tagIds.Count == 0)
        {
            return;
        }

        var creatorId = CurrentUserId.ToStorageText();
        var creationTime = UtcNow;

        var parameters = (
            from hostId in hostIds
            from tagId in tagIds
            select new
            {
                HostId = hostId.ToStorageText(),
                TagId = tagId.ToStorageText(),
                CreationTime = creationTime,
                CreatorId = creatorId,
            }).ToArray();

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                _insertAssignmentSql,
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        IReadOnlyCollection<Guid> hostIds,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostIds);
        ArgumentNullException.ThrowIfNull(tagIds);

        if (hostIds.Count == 0 || tagIds.Count == 0)
        {
            return;
        }

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                _deleteAssignmentSql,
                new
                {
                    HostIds = hostIds.Select(static id => id.ToStorageText()).ToArray(),
                    TagIds = tagIds.Select(static id => id.ToStorageText()).ToArray(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAssignmentsByTagAsync(
        Guid tagId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                _deleteAssignmentsByTagSql,
                new { TagId = tagId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        HostTag tag,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(tag), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        HostTag tag,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(tag), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    private static object ToParameters(HostTag tag) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器，主键不转会按驱动默认的大写形态落库
        Id = tag.Id.ToStorageText(),
        tag.Name,
        tag.Description,
        tag.Color,
        tag.CreationTime,
        CreatorId = tag.CreatorId.ToStorageText(),
        tag.LastModificationTime,
        LastModifierId = tag.LastModifierId.ToStorageText(),
        tag.IsDeleted,
        tag.DeletionTime,
        DeleterId = tag.DeleterId.ToStorageText(),
    };

    /// <summary>
    /// 「标签 → 主机数」计数行。
    /// </summary>
    /// <remarks>
    /// <b>用可写属性而不是位置记录</b>：Dapper 对位置记录走构造器匹配，而 <c>COUNT(1)</c>
    /// 的列类型（Int64）与 <c>int</c> 对不上时会退化成「按列类型找构造器」并失败。
    /// 可写属性走的是「先构造再赋值」路径，类型转换交给 Dapper 自己完成。
    /// </remarks>
    private sealed class TagCountRow
    {
        /// <summary>标签主键。</summary>
        public Guid TagId { get; set; }

        /// <summary>挂载该标签的主机数。</summary>
        public int HostCount { get; set; }
    }
}
