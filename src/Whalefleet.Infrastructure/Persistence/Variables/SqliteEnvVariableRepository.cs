using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Variables;

namespace Whalefleet.Infrastructure.Persistence.Variables;

/// <summary>
/// 环境变量仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>作用域用两个可空列表达</b>：<c>host_id</c> 与 <c>compose_name</c> 全为 NULL 是全局变量，
/// 全非空是项目级变量。数据库里的 CHECK 约束把「半截作用域」挡在库外，
/// 因此应用层不必在每次写入前都记得检查。</para>
/// <para><b>排序</b>：按名称升序（区分大小写——变量名本身就是大小写敏感的，
/// 排序若不区分大小写，<c>TAG</c> 与 <c>tag</c> 的相对次序会随实现而漂移）。</para>
/// <para><b>写入侧显式转文本</b>：Dapper 的参数绑定路径会绕过 <c>ITypeHandler</c>，
/// 主键与审计人必须自己转成小写 36 位（见 <see cref="StorageTextExtensions"/>）。</para>
/// </remarks>
internal sealed class SqliteEnvVariableRepository : RepositoryBase<EnvVariable, Guid>, IEnvVariableRepository
{
    private const string _columns =
        "id, host_id, compose_name, name, encrypted_value, "
        + "is_secret, webhook_overridable, description, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByScopeSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE host_id IS NULL AND compose_name IS NULL
          AND name = @Name AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByProjectScopeSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE host_id = @HostId AND compose_name = @ComposeName
          AND name = @Name AND {SoftDeleteFilter}
        """;

    private static readonly string _listGlobalSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE host_id IS NULL AND compose_name IS NULL AND {SoftDeleteFilter}
        ORDER BY name
        """;

    private static readonly string _listByProjectSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE host_id = @HostId AND compose_name = @ComposeName AND {SoftDeleteFilter}
        ORDER BY name
        """;

    // ⚠️ 括号是必需的：AND 的优先级高于 OR，少了外层括号会变成
    // 「(全局行) OR (项目行 AND 未删除)」——**全局行会绕过软删除过滤**，
    // 已删除的全局变量会重新出现在列表里。
    private static readonly string _listVisibleSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE (
                (host_id IS NULL AND compose_name IS NULL)
                OR (host_id = @HostId AND compose_name = @ComposeName)
              )
          AND {SoftDeleteFilter}
        ORDER BY host_id IS NULL, name
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM env_variables
        WHERE {SoftDeleteFilter}
        ORDER BY host_id IS NULL, host_id, compose_name, name
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO env_variables ({_columns})
        VALUES (@Id, @HostId, @ComposeName, @Name, @EncryptedValue,
                @IsSecret, @WebhookOverridable, @Description,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE env_variables
        SET host_id = @HostId,
            compose_name = @ComposeName,
            name = @Name,
            encrypted_value = @EncryptedValue,
            is_secret = @IsSecret,
            webhook_overridable = @WebhookOverridable,
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
    public SqliteEnvVariableRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<EnvVariable?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<EnvVariable>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<EnvVariable?> FindByScopeAsync(
        Guid? hostId,
        string? composeName,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 全局与项目级走两条 SQL：两者的 WHERE 条件互斥（一个用 IS NULL，一个用等值），
        // 用一条 SQL 拼条件反而更容易写错「NULL 比较恒为假」这种坑。
        if (hostId is null || composeName is null)
        {
            return await connection.QuerySingleOrDefaultAsync<EnvVariable>(
                new CommandDefinition(
                    _selectByScopeSql,
                    new { Name = name.Trim() },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        return await connection.QuerySingleOrDefaultAsync<EnvVariable>(
            new CommandDefinition(
                _selectByProjectScopeSql,
                new { HostId = hostId.Value.ToStorageText(), ComposeName = composeName.Trim(), Name = name.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvVariable>> ListGlobalAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var variables = await connection.QueryAsync<EnvVariable>(
            new CommandDefinition(_listGlobalSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return variables.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvVariable>> ListByProjectAsync(
        Guid hostId,
        string composeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeName);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var variables = await connection.QueryAsync<EnvVariable>(
            new CommandDefinition(
                _listByProjectSql,
                new { HostId = hostId.ToStorageText(), ComposeName = composeName.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return variables.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvVariable>> ListVisibleAsync(
        Guid hostId,
        string composeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeName);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var variables = await connection.QueryAsync<EnvVariable>(
            new CommandDefinition(
                _listVisibleSql,
                new { HostId = hostId.ToStorageText(), ComposeName = composeName.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return variables.AsList();
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        EnvVariable variable,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(variable), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        EnvVariable variable,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(variable), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把变量实体转换为 Dapper 参数。
    /// </summary>
    /// <param name="variable">变量实体。</param>
    /// <returns>Dapper 参数对象。</returns>
    private static object ToParameters(EnvVariable variable) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器，主键不转会按驱动的
        // 大写形态落库（与文档 / 运维脚本里的小写字面量对不上）。
        Id = variable.Id.ToStorageText(),
        HostId = variable.HostId?.ToStorageText(),
        variable.ComposeName,
        variable.Name,
        variable.EncryptedValue,
        variable.IsSecret,
        variable.WebhookOverridable,
        variable.Description,
        variable.CreationTime,
        CreatorId = variable.CreatorId.ToStorageText(),
        variable.LastModificationTime,
        LastModifierId = variable.LastModifierId.ToStorageText(),
        variable.IsDeleted,
        variable.DeletionTime,
        DeleterId = variable.DeleterId.ToStorageText(),
    };
}
