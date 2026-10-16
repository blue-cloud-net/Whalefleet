using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Infrastructure.Persistence.Permissions;

/// <summary>
/// 用户-角色分配仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b><c>host_id</c> 的 NULL 语义在 SQL 里要显式处理</b>：查询全局分配必须写
/// <c>host_id IS NULL</c>，而<b>不能</b>写 <c>host_id = @HostId</c>——
/// 后者与 NULL 比较永远为假，「查全局分配」会静默地永远查不到。
/// 因此参数不是传 Guid 而是传 <see cref="Nullable{T}"/>，由两个不同 SQL 分支承接。</para>
/// <para><b>排序刻意不按时间</b>：两个列表都是「管理界面给人看的清单」，
/// 按 <c>user_id</c> / <c>host_id</c> 排能走索引；按 <c>creation_time</c> 排则每次都要扫后排序。</para>
/// <para><b>不做 N+1 的补救</b>：<see cref="GetEffectivePermissionsAsync"/> 之外的调用点
/// 若需要角色信息，应走批量接口——那正是 <c>IRoleRepository.QueryListByIdsAsync</c> 存在的理由。</para>
/// </remarks>
internal sealed class SqliteUserRoleAssignmentRepository
    : RepositoryBase<UserRoleAssignment, Guid>, IUserRoleAssignmentRepository
{
    private const string _columns =
        "id, user_id, role_id, host_id, creation_time, creator_id, "
        + "last_modification_time, last_modifier_id, is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByUserRoleGlobalSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE user_id = @UserId AND role_id = @RoleId AND host_id IS NULL AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByUserRoleHostSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE user_id = @UserId AND role_id = @RoleId AND host_id = @HostId AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByUserSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE user_id = @UserId AND {SoftDeleteFilter}
        ORDER BY host_id IS NULL DESC, host_id, role_id
        """;

    // host_id IS NULL 的全局分配**不在**这里：它对所有主机生效，无法归属到某一台，
    // 而「谁能访问这台机器」这个问题里它不构成一个具名的人次。
    private static readonly string _selectByHostSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE host_id = @HostId AND {SoftDeleteFilter}
        ORDER BY user_id, role_id
        """;

    private static readonly string _selectByUsersSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE user_id IN @UserIds AND {SoftDeleteFilter}
        ORDER BY user_id, role_id
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM user_role_assignments
        WHERE {SoftDeleteFilter}
        ORDER BY user_id, role_id, host_id
        """;

    private static readonly string _countByRoleSql = $"""
        SELECT COUNT(1)
        FROM user_role_assignments
        WHERE role_id = @RoleId AND {SoftDeleteFilter}
        """;

    private static readonly string _countGlobalByRoleSql = $"""
        SELECT COUNT(1)
        FROM user_role_assignments
        WHERE role_id = @RoleId AND host_id IS NULL AND {SoftDeleteFilter}
        """;

    private static readonly string _countGlobalByUserAndRoleSql = $"""
        SELECT COUNT(1)
        FROM user_role_assignments
        WHERE user_id = @UserId AND role_id = @RoleId AND host_id IS NULL AND {SoftDeleteFilter}
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO user_role_assignments ({_columns})
        VALUES (@Id, @UserId, @RoleId, @HostId, @CreationTime, @CreatorId,
                @LastModificationTime, @LastModifierId, @IsDeleted, @DeletionTime, @DeleterId)
        """;

    // user_id / role_id / host_id 刻意不更新：它们是「这条分配是什么」的身份部分，
    // 改归属等于撤销 + 重新分配（后者才有独立的审计时间点）。
    private static readonly string _updateSql = $"""
        UPDATE user_role_assignments
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
    public SqliteUserRoleAssignmentRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<UserRoleAssignment?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<UserRoleAssignment>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<UserRoleAssignment?> GetAsync(
        Guid userId,
        Guid roleId,
        Guid? hostId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        return hostId is null
            ? await connection.QuerySingleOrDefaultAsync<UserRoleAssignment>(
                new CommandDefinition(
                    _selectByUserRoleGlobalSql,
                    new { UserId = userId.ToStorageText(), RoleId = roleId.ToStorageText() },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false)
            : await connection.QuerySingleOrDefaultAsync<UserRoleAssignment>(
                new CommandDefinition(
                    _selectByUserRoleHostSql,
                    new
                    {
                        UserId = userId.ToStorageText(),
                        RoleId = roleId.ToStorageText(),
                        HostId = hostId.Value.ToStorageText(),
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserRoleAssignment>> ListByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<UserRoleAssignment>(
            new CommandDefinition(
                _selectByUserSql,
                new { UserId = userId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserRoleAssignment>> ListByUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
        {
            return [];
        }

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<UserRoleAssignment>(
            new CommandDefinition(
                _selectByUsersSql,
                new { UserIds = userIds.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserRoleAssignment>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<UserRoleAssignment>(
            new CommandDefinition(
                _selectByHostSql,
                new { HostId = hostId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> CountByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _countByRoleSql,
                new { RoleId = roleId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return checked((int)count);
    }

    /// <inheritdoc />
    public async Task<int> CountGlobalByRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _countGlobalByRoleSql,
                new { RoleId = roleId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return checked((int)count);
    }

    /// <inheritdoc />
    public async Task<int> CountGlobalByUserAndRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _countGlobalByUserAndRoleSql,
                new { UserId = userId.ToStorageText(), RoleId = roleId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return checked((int)count);
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        UserRoleAssignment assignment,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                _insertSql,
                new
                {
                    Id = assignment.Id.ToStorageText(),
                    UserId = assignment.UserId.ToStorageText(),
                    RoleId = assignment.RoleId.ToStorageText(),

                    // 显式转可空：全局分配必须是 SQL NULL 而不是空串，
                    // 否则 ux_user_role_assignments 的 COALESCE(host_id, '') 会把它
                    // 与"作用域是空串"这种非法状态混为一谈
                    HostId = assignment.HostId?.ToStorageText(),

                    assignment.CreationTime,
                    CreatorId = assignment.CreatorId.ToStorageText(),
                    assignment.LastModificationTime,
                    LastModifierId = assignment.LastModifierId.ToStorageText(),
                    assignment.IsDeleted,
                    assignment.DeletionTime,
                    DeleterId = assignment.DeleterId.ToStorageText(),
                },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        UserRoleAssignment assignment,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                _updateSql,
                new
                {
                    Id = assignment.Id.ToStorageText(),
                    assignment.LastModificationTime,
                    LastModifierId = assignment.LastModifierId.ToStorageText(),
                    assignment.IsDeleted,
                    assignment.DeletionTime,
                    DeleterId = assignment.DeleterId.ToStorageText(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }
}
