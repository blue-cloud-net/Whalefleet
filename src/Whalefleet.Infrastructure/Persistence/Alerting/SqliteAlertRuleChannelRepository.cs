using System.Globalization;

using Dapper;

using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Persistence.Alerting;

/// <summary>
/// 告警规则 ↔ 通知渠道关联仓储的 SQLite / Dapper 实现。
/// </summary>
internal sealed class SqliteAlertRuleChannelRepository : IAlertRuleChannelRepository
{
    private const string _timeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly IDbConnectionFactory _connections;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    public SqliteAlertRuleChannelRepository(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetChannelIdsAsync(
        Guid ruleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var ids = await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT channel_id FROM alert_rule_channels WHERE rule_id = @RuleId",
                new { RuleId = ruleId.ToStorageText() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. ids.Select(id => Guid.Parse(id, CultureInfo.InvariantCulture))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetChannelIdsAsync(
        IReadOnlyCollection<Guid> ruleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);

        if (ruleIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<(string RuleId, string ChannelId)>(
            new CommandDefinition(
                "SELECT rule_id, channel_id FROM alert_rule_channels WHERE rule_id IN @RuleIds",
                new { RuleIds = ruleIds.Select(id => id.ToStorageText()).ToArray() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows
            .GroupBy(row => Guid.Parse(row.RuleId, CultureInfo.InvariantCulture))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)[.. group.Select(row =>
                    Guid.Parse(row.ChannelId, CultureInfo.InvariantCulture))]);
    }

    /// <inheritdoc />
    public async Task ReplaceAsync(
        Guid ruleId,
        IReadOnlyCollection<Guid> channelIds,
        Guid creatorId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM alert_rule_channels WHERE rule_id = @RuleId",
            new { RuleId = ruleId.ToStorageText() },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var channelId in channelIds.Distinct())
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO alert_rule_channels (rule_id, channel_id, creation_time, creator_id)
                VALUES (@RuleId, @ChannelId, @CreationTime, @CreatorId)
                """,
                new
                {
                    RuleId = ruleId.ToStorageText(),
                    ChannelId = channelId.ToStorageText(),
                    CreationTime = nowUtc.ToString(_timeFormat, CultureInfo.InvariantCulture),
                    CreatorId = creatorId.ToStorageText(),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
