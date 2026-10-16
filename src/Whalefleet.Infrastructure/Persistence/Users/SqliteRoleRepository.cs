using System.Data.Common;
using System.Globalization;
using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Users;

namespace Whalefleet.Infrastructure.Persistence.Users;

/// <summary>
/// 角色仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>内置角色不可删由数据层兜底</b>：<c>RemoveAsync</c> 的 <c>WHERE</c> 带
/// <c>is_built_in = 0</c>，因此无论调用方走不走 <see cref="Role.Retire"/>，
/// "删掉内置角色"这件事都发生不了。这条不变量不能只靠调用方自觉。</para>
/// <para><b>默认角色唯一由事务保证</b>：<c>SetDefaultAsync</c> 先清掉其他行的默认标记再标记本行，
/// 两步在同一事务里，中途失败不会留下两个默认角色。</para>
/// </remarks>
internal sealed class SqliteRoleRepository : RepositoryBase<Role, Guid>, IRoleRepository
{
    private const string _columns =
        "id, name, display_name, description, is_built_in, is_default, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM roles
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByNameSql = $"""
        SELECT {_columns}
        FROM roles
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByNameSql = $"""
        SELECT COUNT(1)
        FROM roles
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    // ux_roles_default 是 partial 唯一索引，命中至多一行
    private static readonly string _selectDefaultSql = $"""
        SELECT {_columns}
        FROM roles
        WHERE is_default = 1 AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM roles
        WHERE {SoftDeleteFilter}
        ORDER BY is_built_in DESC, name
        """;

    private static readonly string _countSql = $"""
        SELECT COUNT(1)
        FROM roles
        WHERE {SoftDeleteFilter}
        """;

    // 批量按主键查询：分配列表补齐展示字段用。IN 子句走主键索引。
    private static readonly string _selectByIdsSql = $"""
        SELECT {_columns}
        FROM roles
        WHERE id IN @Ids AND {SoftDeleteFilter}
        """;

    private static readonly string _selectPermissionsSql = """
        SELECT permission
        FROM role_permissions
        WHERE role_id = @RoleId
        ORDER BY permission
        """;

    private static readonly string _deletePermissionsSql = """
        DELETE FROM role_permissions
        WHERE role_id = @RoleId
        """;

    private static readonly string _insertPermissionSql = """
        INSERT INTO role_permissions (role_id, permission, creation_time, creator_id,
                                      last_modification_time, last_modifier_id)
        VALUES (@RoleId, @Permission, @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId)
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO roles ({_columns})
        VALUES (@Id, @Name, @DisplayName, @Description, @IsBuiltIn, @IsDefault,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = """
        UPDATE roles
        SET display_name = @DisplayName,
            description = @Description,
            last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId
        WHERE id = @Id AND is_deleted = 0
        """;

    private static readonly string _clearDefaultSql = """
        UPDATE roles
        SET is_default = 0
        WHERE is_default = 1 AND id <> @Id
        """;

    private static readonly string _setDefaultSql = """
        UPDATE roles
        SET is_default = 1, last_modification_time = @LastModificationTime, last_modifier_id = @LastModifierId
        WHERE id = @Id AND is_deleted = 0
        """;

    private static readonly string _deleteSql = """
        UPDATE roles
        SET is_deleted = 1,
            deletion_time = @DeletionTime,
            deleter_id = @DeleterId,
            is_default = 0,
            last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId
        WHERE id = @Id AND is_deleted = 0 AND is_built_in = 0
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteRoleRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<Role?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<Role>(
            new CommandDefinition(_selectByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Role>(
            new CommandDefinition(
                _selectByNameSql,
                new { Name = name.Trim().ToLowerInvariant() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Role>> QueryListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<Role>(
            new CommandDefinition(
                _selectByIdsSql,
                new { Ids = ids.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                _selectPermissionsSql,
                new { RoleId = roleId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<bool> ReplacePermissionsAsync(
        Guid roleId,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 先确认目标存在且未删除：给一个不存在的角色「清空权限」不应该被当成成功，
        // 否则调用方会以为自己改到了某个实际生效的角色上。
        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT COUNT(1) FROM roles WHERE id = @Id AND is_deleted = 0";
            AddParameter(existsCommand, "@Id", roleId.ToStorageText());

            var exists = Convert.ToInt64(
                await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);

            if (exists == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                _deletePermissionsSql,
                new { RoleId = roleId.ToStorageText() },
                transaction: transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var now = UtcNow;
        var operatorId = CurrentUserId.ToStorageText();

        foreach (var permission in permissions)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    _insertPermissionSql,
                    new
                    {
                        RoleId = roleId.ToStorageText(),
                        Permission = permission,
                        CreationTime = now,
                        CreatorId = operatorId,
                        LastModificationTime = (DateTime?)null,
                        LastModifierId = operatorId,
                    },
                    transaction: transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _existsByNameSql,
                new { Name = name.Trim().ToLowerInvariant() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    public async Task<Role?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Role>(
            new CommandDefinition(_selectDefaultSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        Role role,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(role), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        Role role,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, new
            {
                Id = role.Id.ToStorageText(),
                role.DisplayName,
                role.Description,
                role.LastModificationTime,
                LastModifierId = role.LastModifierId.ToStorageText(),
            },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var operatorId = CurrentUserId;

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 先确认目标存在且未删除，避免"把不存在的角色设默认"被算成成功
        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT COUNT(1) FROM roles WHERE id = @Id AND is_deleted = 0";
            AddParameter(existsCommand, "@Id", id.ToStorageText());

            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is null or DBNull || Convert.ToInt64(exists, CultureInfo.InvariantCulture) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var clearCommand = connection.CreateCommand())
        {
            clearCommand.Transaction = transaction;
            clearCommand.CommandText = _clearDefaultSql;
            AddParameter(clearCommand, "@Id", id.ToStorageText());
            await clearCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.Transaction = transaction;
            setCommand.CommandText = _setDefaultSql;
            AddParameter(setCommand, "@Id", id.ToStorageText());
            AddParameter(setCommand, "@LastModificationTime", now);
            AddParameter(setCommand, "@LastModifierId", operatorId.ToStorageText());
            await setCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// <b>覆写而非用基类模板</b>：角色表有两条基类模板没有的规则——
    /// 内置角色不可删（<c>WHERE is_built_in = 0</c>），以及删除时必须清掉 <c>is_default</c>
    /// （否则默认角色被删后，"创建用户取默认角色"就会悬空）。
    /// </remarks>
    public override async Task<bool> RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var role = await QueryByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return false;
        }

        MarkDeleted(role);
        MarkModified(role);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                _deleteSql,
                new
                {
                    Id = role.Id.ToStorageText(),
                    role.DeletionTime,
                    DeleterId = role.DeleterId.ToStorageText(),
                    role.LastModificationTime,
                    LastModifierId = role.LastModifierId.ToStorageText(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (affected != 1)
        {
            return false;
        }

        // 权限行一并清掉：留下它们会让「角色名可再次使用」时新角色继承上一任的权限，
        // 那是一次不经意的提权。role_permissions 没有外键到 roles 的级联，
        // 因此这里必须显式删。
        await connection.ExecuteAsync(
            new CommandDefinition(
                _deletePermissionsSql,
                new { RoleId = role.Id.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return true;
    }

    private static object ToParameters(Role role) => new
    {
        Id = role.Id.ToStorageText(),
        role.Name,
        role.DisplayName,
        role.Description,
        role.IsBuiltIn,
        role.IsDefault,
        role.CreationTime,
        CreatorId = role.CreatorId.ToStorageText(),
        role.LastModificationTime,
        LastModifierId = role.LastModifierId.ToStorageText(),
        role.IsDeleted,
        role.DeletionTime,
        DeleterId = role.DeleterId.ToStorageText(),
    };
}