using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Webhooks;

namespace Whalefleet.Infrastructure.Persistence.Webhooks;

/// <summary>
/// 容器重建 Webhook 仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>令牌反查走唯一索引</b>：<c>ux_container_webhooks_token</c> 是部分唯一索引
/// （<c>WHERE is_deleted = 0 AND webhook_enabled = 1</c>），因此反查必须带上同样的过滤条件——
/// 否则会命中「已吊销但哈希未清」的历史行。</para>
/// <para><b>写入侧显式转文本</b>：Dapper 的参数绑定路径会绕过 <c>ITypeHandler</c>，
/// 主键与审计人必须自己转成小写 36 位（见 <see cref="StorageTextExtensions"/>）。</para>
/// </remarks>
internal sealed class SqliteContainerWebhookRepository
    : RepositoryBase<ContainerWebhook, Guid>, IContainerWebhookRepository
{
    private const string _columns =
        "id, host_id, container_name, "
        + "webhook_token_hash, webhook_token_hint, webhook_enabled, webhook_last_used_at, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM container_webhooks
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _selectByHostAndContainerSql = $"""
        SELECT {_columns}
        FROM container_webhooks
        WHERE host_id = @HostId AND container_name = @ContainerName AND {SoftDeleteFilter}
        """;

    // 令牌反查：过滤条件与 ux_container_webhooks_token 部分索引的定义保持一致
    private static readonly string _selectByTokenSql = $"""
        SELECT {_columns}
        FROM container_webhooks
        WHERE webhook_token_hash = @TokenHash
          AND webhook_enabled = 1
          AND {SoftDeleteFilter}
        """;

    private static readonly string _listByHostSql = $"""
        SELECT {_columns}
        FROM container_webhooks
        WHERE host_id = @HostId AND {SoftDeleteFilter}
        ORDER BY container_name
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM container_webhooks
        WHERE {SoftDeleteFilter}
        ORDER BY host_id, container_name
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO container_webhooks ({_columns})
        VALUES (@Id, @HostId, @ContainerName,
                @WebhookTokenHash, @WebhookTokenHint, @WebhookEnabled, @WebhookLastUsedAt,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE container_webhooks
        SET host_id = @HostId,
            container_name = @ContainerName,
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
    public SqliteContainerWebhookRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<ContainerWebhook?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<ContainerWebhook>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<ContainerWebhook?> FindByHostAndContainerAsync(
        Guid hostId,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ContainerWebhook>(
            new CommandDefinition(
                _selectByHostAndContainerSql,
                new { HostId = hostId.ToStorageText(), ContainerName = containerName.Trim().TrimStart('/') },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContainerWebhook?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ContainerWebhook>(
            new CommandDefinition(
                _selectByTokenSql,
                new { TokenHash = tokenHash.Trim().ToLowerInvariant() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContainerWebhook>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var webhooks = await connection.QueryAsync<ContainerWebhook>(
            new CommandDefinition(
                _listByHostSql,
                new { HostId = hostId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return webhooks.AsList();
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        ContainerWebhook webhook,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(webhook), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        ContainerWebhook webhook,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(webhook), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把 Webhook 实体转换为 Dapper 参数。
    /// </summary>
    /// <param name="webhook">Webhook 实体。</param>
    /// <returns>Dapper 参数对象。</returns>
    private static object ToParameters(ContainerWebhook webhook) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器，主键不转会按驱动的
        // 大写形态落库（与文档 / 运维脚本里的小写字面量对不上）。
        Id = webhook.Id.ToStorageText(),
        HostId = webhook.HostId.ToStorageText(),
        webhook.ContainerName,
        webhook.WebhookTokenHash,
        webhook.WebhookTokenHint,
        webhook.WebhookEnabled,
        webhook.WebhookLastUsedAt,
        webhook.CreationTime,
        CreatorId = webhook.CreatorId.ToStorageText(),
        webhook.LastModificationTime,
        LastModifierId = webhook.LastModifierId.ToStorageText(),
        webhook.IsDeleted,
        webhook.DeletionTime,
        DeleterId = webhook.DeleterId.ToStorageText(),
    };
}
