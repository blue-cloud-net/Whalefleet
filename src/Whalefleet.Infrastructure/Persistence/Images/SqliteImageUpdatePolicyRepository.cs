using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Images;

namespace Whalefleet.Infrastructure.Persistence.Images;

/// <summary>
/// 镜像自动更新策略仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>写入侧显式转文本</b>：Dapper 的参数绑定路径会绕过 <c>ITypeHandler</c>，
/// 主键、枚举与审计人必须自己转（见 <see cref="StorageTextExtensions"/>）。</para>
/// <para><b>（主机, 镜像引用）唯一</b>：由部分唯一索引
/// <c>ux_image_update_policies_host_reference</c> 兜底，
/// 只覆盖 <c>is_deleted = 0</c>——软删除之后同名策略可以重建。</para>
/// </remarks>
internal sealed class SqliteImageUpdatePolicyRepository
    : RepositoryBase<ImageUpdatePolicy, Guid>, IImageUpdatePolicyRepository
{
    private const string _columns =
        "id, host_id, image_reference, mode, is_enabled, "
        + "baseline_digest, remote_digest, last_state, last_message, "
        + "last_checked_at, last_applied_at, last_notified_digest, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM image_update_policies
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _listByHostSql = $"""
        SELECT {_columns}
        FROM image_update_policies
        WHERE host_id = @HostId AND {SoftDeleteFilter}
        ORDER BY image_reference
        """;

    private static readonly string _listEnabledSql = $"""
        SELECT {_columns}
        FROM image_update_policies
        WHERE is_enabled = 1 AND {SoftDeleteFilter}
        ORDER BY host_id, image_reference
        """;

    private static readonly string _findSql = $"""
        SELECT {_columns}
        FROM image_update_policies
        WHERE host_id = @HostId AND image_reference = @Reference AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM image_update_policies
        WHERE {SoftDeleteFilter}
        ORDER BY host_id, image_reference
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO image_update_policies ({_columns})
        VALUES (@Id, @HostId, @ImageReference, @Mode, @IsEnabled,
                @BaselineDigest, @RemoteDigest, @LastState, @LastMessage,
                @LastCheckedAt, @LastAppliedAt, @LastNotifiedDigest,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE image_update_policies
        SET image_reference = @ImageReference,
            mode = @Mode,
            is_enabled = @IsEnabled,
            baseline_digest = @BaselineDigest,
            remote_digest = @RemoteDigest,
            last_state = @LastState,
            last_message = @LastMessage,
            last_checked_at = @LastCheckedAt,
            last_applied_at = @LastAppliedAt,
            last_notified_digest = @LastNotifiedDigest,
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
    public SqliteImageUpdatePolicyRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<ImageUpdatePolicy?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<ImageUpdatePolicy>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageUpdatePolicy>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var policies = await connection.QueryAsync<ImageUpdatePolicy>(
            new CommandDefinition(
                _listByHostSql,
                new { HostId = hostId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return policies.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageUpdatePolicy>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var policies = await connection.QueryAsync<ImageUpdatePolicy>(
            new CommandDefinition(
                _listEnabledSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return policies.AsList();
    }

    /// <inheritdoc />
    public async Task<ImageUpdatePolicy?> FindAsync(
        Guid hostId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ImageUpdatePolicy>(
            new CommandDefinition(
                _findSql,
                new { HostId = hostId.ToStorageText(), Reference = reference.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        ImageUpdatePolicy policy,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(policy), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        ImageUpdatePolicy policy,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(policy), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把策略实体转换为 Dapper 参数。
    /// </summary>
    /// <param name="policy">策略实体。</param>
    /// <returns>Dapper 参数对象。</returns>
    private static object ToParameters(ImageUpdatePolicy policy) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器。
        Id = policy.Id.ToStorageText(),
        HostId = policy.HostId.ToStorageText(),
        policy.ImageReference,
        Mode = policy.Mode.ToStorageText(),
        IsEnabled = policy.Enabled,
        policy.BaselineDigest,
        policy.RemoteDigest,
        LastState = ImageUpdateStateText.ToText(policy.State),
        policy.LastMessage,
        policy.LastCheckedAt,
        policy.LastAppliedAt,
        policy.LastNotifiedDigest,
        policy.CreationTime,
        CreatorId = policy.CreatorId.ToStorageText(),
        policy.LastModificationTime,
        LastModifierId = policy.LastModifierId.ToStorageText(),
        policy.IsDeleted,
        policy.DeletionTime,
        DeleterId = policy.DeleterId.ToStorageText(),
    };
}
