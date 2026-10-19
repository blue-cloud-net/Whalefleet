using System.Data.Common;
using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Registries;

namespace Whalefleet.Infrastructure.Persistence.Registries;

/// <summary>
/// Registry 配置仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>按地址查找时用 <c>COLLATE NOCASE</c></b>：地址在写入前已被
/// <see cref="RegistryServerAddress"/> 规范化为小写，但查询方未必走过同一条路径
/// （例如从镜像引用里切出来的地址段）。大小写不敏感匹配可以避免
/// 「明明配了却找不到」的静默失败。</para>
/// <para><b>软删除过滤</b>：全部查询都带 <c>is_deleted = 0</c>，
/// 与唯一索引的部分条件保持一致——否则会出现「索引认为名字被占着，查询却查不到」的错位。</para>
/// </remarks>
internal sealed class SqliteRegistryCredentialRepository :
    RepositoryBase<RegistryCredential, Guid>,
    IRegistryCredentialRepository
{
    private const string _columns =
        "id, name, server_address, username, encrypted_password, description, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM registries
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByNameSql = $"""
        SELECT {_columns}
        FROM registries
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByServerAddressSql = $"""
        SELECT {_columns}
        FROM registries
        WHERE server_address = @ServerAddress COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByNameSql = $"""
        SELECT COUNT(1)
        FROM registries
        WHERE name = @Name COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _existsByServerAddressSql = $"""
        SELECT COUNT(1)
        FROM registries
        WHERE server_address = @ServerAddress COLLATE NOCASE AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM registries
        WHERE {SoftDeleteFilter}
        ORDER BY name
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO registries ({_columns})
        VALUES (@Id, @Name, @ServerAddress, @Username, @EncryptedPassword, @Description,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = """
        UPDATE registries
        SET name = @Name,
            server_address = @ServerAddress,
            username = @Username,
            encrypted_password = @EncryptedPassword,
            description = @Description,
            last_modification_time = @LastModificationTime,
            last_modifier_id = @LastModifierId,
            is_deleted = @IsDeleted,
            deletion_time = @DeletionTime,
            deleter_id = @DeleterId
        WHERE id = @Id AND is_deleted = 0
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteRegistryCredentialRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<RegistryCredential?> QueryByIdAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<RegistryCredential>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<RegistryCredential?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RegistryCredential>(
            new CommandDefinition(
                _selectByNameSql,
                new { Name = name.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RegistryCredential?> FindByServerAddressAsync(
        string serverAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RegistryCredential>(
            new CommandDefinition(
                _selectByServerAddressSql,
                new { ServerAddress = serverAddress.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _existsByNameSql,
                new { Name = name.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByServerAddressAsync(
        string serverAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                _existsByServerAddressSql,
                new { ServerAddress = serverAddress.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        DbConnection connection,
        RegistryCredential entity,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(entity), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        DbConnection connection,
        RegistryCredential entity,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(entity), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把实体映射成写库参数（<b>Guid 必须显式转存储文本</b>）。
    /// </summary>
    /// <remarks>
    /// <b>不能直接把实体当参数传</b>：Dapper 在<b>参数绑定</b>路径上会绕过
    /// <c>ITypeHandler</c>（值类型直接装箱绑定），于是 <c>Id</c> 会被驱动按它自己的
    /// Guid 形态写库（通常是大写），而查询走的是 <c>ToStorageText()</c>（小写 36 位）。
    /// 两者对不上，于是「刚插进去的行按主键查不到」——
    /// 表现是删除返回 404、而列表里明明有那条记录。
    /// </remarks>
    private static object ToParameters(RegistryCredential entity) => new
    {
        Id = entity.Id.ToStorageText(),
        entity.Name,
        entity.ServerAddress,
        entity.Username,
        entity.EncryptedPassword,
        entity.Description,
        entity.CreationTime,
        CreatorId = entity.CreatorId.ToStorageText(),
        entity.LastModificationTime,
        LastModifierId = entity.LastModifierId.ToStorageText(),
        entity.IsDeleted,
        entity.DeletionTime,
        DeleterId = entity.DeleterId.ToStorageText(),
    };
}
