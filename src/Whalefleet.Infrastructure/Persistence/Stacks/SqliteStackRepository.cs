using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Stacks;

namespace Whalefleet.Infrastructure.Persistence.Stacks;

/// <summary>
/// 编排 Stack 仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>排序</b>：全量列表按名称升序（区分大小写）。Stack 名称本身只允许小写字母与数字，
/// 因此「区分大小写」与「不区分」在这里没有实际差别——但 SQL 里写明 <c>ORDER BY name</c>
/// 仍然必要：没有显式排序时 SQLite 的返回顺序是未定义的，
/// 而列表接口要拿它跟「按项目名分组的守护进程容器」对齐。</para>
/// <para><b>Webhook 令牌反查走唯一索引</b>：<c>ux_stacks_webhook_token</c> 是部分唯一索引
/// （<c>WHERE is_deleted = 0 AND webhook_enabled = 1</c>），因此反查必须带上同样的过滤条件——
/// 否则会命中「已吊销但哈希未清」的历史行（虽然当前实现吊销即清哈希，
/// 但索引的定义才是这条查询快的原因）。</para>
/// <para><b>写入侧显式转文本</b>：Dapper 的参数绑定路径会绕过 <c>ITypeHandler</c>，
/// 主键与审计人必须自己转成小写 36 位（见 <see cref="StorageTextExtensions"/>）。</para>
/// </remarks>
internal sealed class SqliteStackRepository : RepositoryBase<Stack, Guid>, IStackRepository
{
    private const string _columns =
        "id, host_id, name, compose_content, "
        + "webhook_token_hash, webhook_token_hint, webhook_enabled, webhook_last_used_at, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM stacks
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByHostAndNameSql = $"""
        SELECT {_columns}
        FROM stacks
        WHERE host_id = @HostId AND name = @Name AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByHostSql = $"""
        SELECT {_columns}
        FROM stacks
        WHERE host_id = @HostId AND {SoftDeleteFilter}
        ORDER BY name
        """;

    // 令牌反查：过滤条件与 ux_stacks_webhook_token 部分索引的定义保持一致
    private static readonly string _selectByWebhookTokenSql = $"""
        SELECT {_columns}
        FROM stacks
        WHERE webhook_token_hash = @TokenHash
          AND webhook_enabled = 1
          AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM stacks
        WHERE {SoftDeleteFilter}
        ORDER BY name
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO stacks ({_columns})
        VALUES (@Id, @HostId, @Name, @ComposeContent,
                @WebhookTokenHash, @WebhookTokenHint, @WebhookEnabled, @WebhookLastUsedAt,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE stacks
        SET host_id = @HostId,
            name = @Name,
            compose_content = @ComposeContent,
            webhook_token_hash = @WebhookTokenHash,
            webhook_token_hint = @WebhookTokenHint,
            webhook_enabled = @WebhookEnabled,
            webhook_last_used_at = @WebhookLastUsedAt,
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
    public SqliteStackRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<Stack?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<Stack>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<Stack?> FindByHostAndNameAsync(
        Guid hostId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Stack>(
            new CommandDefinition(
                _selectByHostAndNameSql,
                new { HostId = hostId.ToStorageText(), Name = name.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Stack>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var stacks = await connection.QueryAsync<Stack>(
            new CommandDefinition(
                _selectByHostSql,
                new { HostId = hostId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return stacks.AsList();
    }

    /// <inheritdoc />
    public async Task<Stack?> FindByWebhookTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<Stack>(
            new CommandDefinition(
                _selectByWebhookTokenSql,
                new { TokenHash = tokenHash.Trim().ToLowerInvariant() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        Stack stack,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(stack), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        Stack stack,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(stack), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把 Stack 实体转换为 Dapper 参数。
    /// </summary>
    /// <param name="stack">Stack 实体。</param>
    /// <returns>Dapper 参数对象。</returns>
    private static object ToParameters(Stack stack) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器，主键不转会按驱动默认的大写形态落库
        Id = stack.Id.ToStorageText(),
        HostId = stack.HostId.ToStorageText(),
        stack.Name,
        stack.ComposeContent,
        stack.WebhookTokenHash,
        stack.WebhookTokenHint,
        stack.WebhookEnabled,
        stack.WebhookLastUsedAt,
        stack.CreationTime,
        CreatorId = stack.CreatorId.ToStorageText(),
        stack.LastModificationTime,
        LastModifierId = stack.LastModifierId.ToStorageText(),
        stack.IsDeleted,
        stack.DeletionTime,
        DeleterId = stack.DeleterId.ToStorageText(),
    };
}
