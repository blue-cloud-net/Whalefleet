using Dapper;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Core.Users;

namespace Docker.Manager.Infrastructure.Persistence.Users;

/// <summary>
/// 用户仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>显式列名</b>：一律写出列清单，不用 <c>SELECT *</c>——列顺序变化不该影响映射，
/// 且新增列时能立刻看出哪些查询需要同步。</para>
/// <para><b>显式参数对象</b>：不把实体直接交给 Dapper 绑定，而是显式列出要写的字段。
/// 理由：Dapper 的参数绑定只认<b>公开</b>属性，而 Dapper 的<b>物化</b>能写非公开属性，
/// 两者不对称；若依赖"传实体自动绑定"，一旦实体有了计算属性（如 <c>DockerHost.Credentials</c>）
/// 就会在绑定阶段炸掉。</para>
/// <para><b>软删除</b>：所有读取与更新都追加 <c>is_deleted = 0</c>；删除本身是 <c>UPDATE</c>
/// 而非 <c>DELETE</c>；用户名的唯一性交给"部分唯一索引"（见 0001_users.sql）。</para>
/// </remarks>
internal sealed class SqliteUserRepository : RepositoryBase<User, Guid>, IUserRepository
{
    private const string _columns =
        "id, username, password_hash, role_id, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM users
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByUsernameSql = $"""
        SELECT {_columns}
        FROM users
        WHERE username = @Username COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByUsernameSql = $"""
        SELECT COUNT(1)
        FROM users
        WHERE username = @Username COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    // 全量不分页：供基类的 QueryAllListAsync 模板使用；分页版见下方 _listSql
    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM users
        WHERE {SoftDeleteFilter}
        ORDER BY creation_time, username
        """;

    private static readonly string _listSql = $"""
        SELECT {_columns}
        FROM users
        WHERE {SoftDeleteFilter}
        ORDER BY creation_time, username
        LIMIT @Take OFFSET @Skip
        """;

    private static readonly string _countSql = $"""
        SELECT COUNT(1)
        FROM users
        WHERE {SoftDeleteFilter}
        """;

    private static readonly string _countByRoleIdSql = $"""
        SELECT COUNT(1)
        FROM users
        WHERE role_id = @RoleId AND {SoftDeleteFilter}
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO users ({_columns})
        VALUES (@Id, @Username, @PasswordHash, @RoleId, @CreationTime, @CreatorId,
                @LastModificationTime, @LastModifierId, @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE users
        SET username = @Username,
            password_hash = @PasswordHash,
            role_id = @RoleId,
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
    public SqliteUserRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<User?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(_selectByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<User?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(_selectByUsernameSql, new { Username = username.Trim() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(_existsByUsernameSql, new { Username = username.Trim() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> ListAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var users = await connection.QueryAsync<User>(
            new CommandDefinition(_listSql, new { Skip = skip, Take = take }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return users.AsList();
    }

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
    public async Task<int> CountByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _countByRoleIdSql,
                new { RoleId = roleId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return checked((int)count);
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        User user,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(user), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        User user,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(user), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    private static object ToParameters(User user) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器（值类型直接装箱），
        // 不转的话主键会按驱动默认的大写形态落库。
        Id = user.Id.ToStorageText(),
        user.Username,
        user.PasswordHash,
        RoleId = user.RoleId.ToStorageText(),
        user.CreationTime,
        CreatorId = user.CreatorId.ToStorageText(),
        user.LastModificationTime,
        LastModifierId = user.LastModifierId.ToStorageText(),
        user.IsDeleted,
        user.DeletionTime,
        DeleterId = user.DeleterId.ToStorageText(),
    };
}
