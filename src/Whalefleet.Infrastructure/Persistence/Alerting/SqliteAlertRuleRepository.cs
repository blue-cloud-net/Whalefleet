using System.Data.Common;

using Dapper;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Persistence.Alerting;

/// <summary>
/// 告警规则仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>为什么用表达式唯一索引</b>：SQLite 唯一索引把 <c>NULL</c> 视为互不相等，
/// 直接对 <c>(host_id, kind)</c> 建唯一索引会让多条「全局 CPU 规则」全部通过。
/// 迁移里用 <c>COALESCE(host_id, '')</c> 把全局归一成一个可比较的值。</para>
/// <para><b>写入侧显式转文本</b>：Dapper 的参数绑定会绕过 <c>ITypeHandler</c>，
/// 主键 / 审计人 / 枚举都必须自己转（见 <see cref="StorageTextExtensions"/>）。</para>
/// </remarks>
internal sealed class SqliteAlertRuleRepository : RepositoryBase<AlertRule, Guid>, IAlertRuleRepository
{
    private const string _columns =
        "id, host_id, kind, enabled, info_threshold, warning_threshold, critical_threshold, "
        + "severity, consecutive_samples, window_seconds, cooldown_seconds, "
        + "creation_time, creator_id, last_modification_time, last_modifier_id, "
        + "is_deleted, deletion_time, deleter_id";

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM alert_rules
        WHERE id = @Id AND {SoftDeleteFilter}
        """;

    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM alert_rules
        WHERE {SoftDeleteFilter}
        ORDER BY host_id IS NOT NULL, kind
        """;

    private static readonly string _selectForHostSql = $"""
        SELECT {_columns}
        FROM alert_rules
        WHERE {SoftDeleteFilter}
          AND (host_id IS NULL OR host_id = @HostId)
        ORDER BY host_id IS NOT NULL, kind
        """;

    private static readonly string _selectGlobalSql = $"""
        SELECT {_columns}
        FROM alert_rules
        WHERE {SoftDeleteFilter} AND host_id IS NULL
        ORDER BY kind
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO alert_rules ({_columns})
        VALUES (@Id, @HostId, @Kind, @Enabled, @InfoThreshold, @WarningThreshold, @CriticalThreshold,
                @Severity, @ConsecutiveSamples, @WindowSeconds, @CooldownSeconds,
                @CreationTime, @CreatorId, @LastModificationTime, @LastModifierId,
                @IsDeleted, @DeletionTime, @DeleterId)
        """;

    private static readonly string _updateSql = $"""
        UPDATE alert_rules
        SET host_id = @HostId,
            kind = @Kind,
            enabled = @Enabled,
            info_threshold = @InfoThreshold,
            warning_threshold = @WarningThreshold,
            critical_threshold = @CriticalThreshold,
            severity = @Severity,
            consecutive_samples = @ConsecutiveSamples,
            window_seconds = @WindowSeconds,
            cooldown_seconds = @CooldownSeconds,
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
    public SqliteAlertRuleRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<AlertRule?> QueryByIdAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<AlertRule>(
            new CommandDefinition(
                _selectByIdSql,
                new { Id = id.ToStorageText() },
                cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertRule>> ListForHostAsync(
        Guid? hostId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        string sql;
        object parameters;

        if (hostId is null)
        {
            sql = _selectGlobalSql;
            parameters = new { };
        }
        else
        {
            sql = _selectForHostSql;
            parameters = new { HostId = hostId.Value.ToStorageText() };
        }

        var rules = await connection.QueryAsync<AlertRule>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rules];
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        Guid? hostId,
        AlertKind kind,
        Guid? excludeId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT COUNT(1)
            FROM alert_rules
            WHERE {SoftDeleteFilter}
              AND COALESCE(host_id, '') = @HostId
              AND kind = @Kind
              AND (@ExcludeId IS NULL OR id <> @ExcludeId)
            """;

        await using var connection = this.Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new
                {
                    HostId = hostId?.ToStorageText() ?? String.Empty,
                    Kind = kind.ToString(),
                    ExcludeId = excludeId?.ToStorageText(),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return count > 0;
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        DbConnection connection,
        AlertRule rule,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(rule), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        DbConnection connection,
        AlertRule rule,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_updateSql, ToParameters(rule), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把实体转换为 Dapper 参数。
    /// </summary>
    private static object ToParameters(AlertRule rule) => new
    {
        Id = rule.Id.ToStorageText(),
        HostId = rule.HostId?.ToStorageText(),
        Kind = rule.Kind.ToString(),
        rule.Enabled,
        rule.InfoThreshold,
        rule.WarningThreshold,
        rule.CriticalThreshold,
        Severity = rule.Severity.ToStorageText(),
        rule.ConsecutiveSamples,
        rule.WindowSeconds,
        rule.CooldownSeconds,
        rule.CreationTime,
        CreatorId = rule.CreatorId.ToStorageText(),
        rule.LastModificationTime,
        LastModifierId = rule.LastModifierId.ToStorageText(),
        rule.IsDeleted,
        rule.DeletionTime,
        DeleterId = rule.DeleterId.ToStorageText(),
    };
}
