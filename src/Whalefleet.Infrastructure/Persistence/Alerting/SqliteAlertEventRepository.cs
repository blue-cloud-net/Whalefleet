using System.Globalization;

using Dapper;

using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Persistence.Alerting;

/// <summary>
/// 告警事件仓储的 SQLite / Dapper 实现（只追加 + 冷却查询）。
/// </summary>
internal sealed class SqliteAlertEventRepository : IAlertEventRepository
{
    private const string _timeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly IDbConnectionFactory _connections;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    public SqliteAlertEventRepository(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async Task AddAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        const string sql = """
            INSERT INTO alert_events
                (id, rule_id, rule_kind, target_key, host_id, host_name, container_id, container_name,
                 severity, metric_value, threshold_value, occurred_at)
            VALUES
                (@Id, @RuleId, @RuleKind, @TargetKey, @HostId, @HostName, @ContainerId, @ContainerName,
                 @Severity, @MetricValue, @ThresholdValue, @OccurredAt)
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = alertEvent.Id.ToStorageText(),
                RuleId = alertEvent.RuleId.ToStorageText(),
                RuleKind = alertEvent.RuleKind.ToString(),
                alertEvent.TargetKey,
                HostId = alertEvent.HostId?.ToStorageText(),
                alertEvent.HostName,
                alertEvent.ContainerId,
                alertEvent.ContainerName,
                Severity = alertEvent.Severity.ToStorageText(),
                alertEvent.MetricValue,
                alertEvent.ThresholdValue,
                OccurredAt = Format(alertEvent.OccurredAt),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLastOccurredAtAsync(
        Guid ruleId,
        string targetKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        const string sql = """
            SELECT occurred_at
            FROM alert_events
            WHERE rule_id = @RuleId AND target_key = @TargetKey
            ORDER BY occurred_at DESC
            LIMIT 1
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(
                sql,
                new { RuleId = ruleId.ToStorageText(), TargetKey = targetKey.Trim() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> TrimAsync(DateTime beforeUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM alert_events WHERE occurred_at < @Before",
            new { Before = Format(beforeUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 统一时间格式：让字典序等于时间序。
    /// </summary>
    private static string Format(DateTime value) =>
        value.ToString(_timeFormat, CultureInfo.InvariantCulture);
}
