using System.Globalization;
using System.Text;

using Dapper;

using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Alerting;

namespace Whalefleet.Infrastructure.Persistence.Alerting;

/// <summary>
/// 站内通知仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>可见性过滤必须在 SQL 里做</b>：先查全量再在内存里剔除，
/// 会让「总数 / 未读数」把用户看不到的通知也算进去——红点永远清不掉。</para>
/// <para><b>已读 / 删除用 upsert</b>：同一条通知可能被多次标记，
/// <c>ON CONFLICT ... DO UPDATE</c> 让操作幂等，且不会因为并发插入失败。</para>
/// <para><b>时间统一格式</b>：字典序 = 时间序（见审计仓储的说明）。</para>
/// </remarks>
internal sealed class SqliteNotificationRepository : INotificationRepository
{
    private const string _timeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string _columns =
        "n.id, n.source_kind, n.severity, n.message_key, n.params_json, "
        + "n.host_id, n.host_name, n.dedupe_key, n.occurred_at";

    private readonly IDbConnectionFactory _connections;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    public SqliteNotificationRepository(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        const string sql = """
            INSERT OR IGNORE INTO notifications
                (id, source_kind, severity, message_key, params_json, host_id, host_name, dedupe_key, occurred_at)
            VALUES
                (@Id, @SourceKind, @Severity, @MessageKey, @ParamsJson, @HostId, @HostName, @DedupeKey, @OccurredAt)
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = notification.Id.ToStorageText(),
                SourceKind = notification.SourceKind.ToString(),
                Severity = notification.Severity.ToStorageText(),
                notification.MessageKey,
                notification.ParamsJson,
                HostId = notification.HostId?.ToStorageText(),
                notification.HostName,
                notification.DedupeKey,
                OccurredAt = Format(notification.OccurredAt),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Notification?> QueryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT {_columns} FROM notifications n WHERE n.id = @Id";

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<NotificationRow>(
            new CommandDefinition(sql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToNotification();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationListItem>> ListAsync(
        Guid userId,
        NotificationFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (where, parameters) = BuildWhere(filter, includeDismissed: false);
        parameters.Add("UserId", userId.ToStorageText());
        parameters.Add("Skip", offset);
        parameters.Add("Take", limit);

        var sql = $"""
            SELECT {_columns}, s.read_at, s.dismissed_at
            FROM notifications n
            LEFT JOIN notification_user_states s
                   ON s.notification_id = n.id AND s.user_id = @UserId
            WHERE {where}
            ORDER BY n.occurred_at DESC, n.id DESC
            LIMIT @Take OFFSET @Skip
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<NotificationRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows.Select(row => new NotificationListItem(
            row.ToNotification(),
            row.ReadAt,
            row.DismissedAt))];
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(
        Guid userId,
        NotificationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (where, parameters) = BuildWhere(filter, includeDismissed: false);
        parameters.Add("UserId", userId.ToStorageText());

        var sql = $"""
            SELECT COUNT(1)
            FROM notifications n
            LEFT JOIN notification_user_states s
                   ON s.notification_id = n.id AND s.user_id = @UserId
            WHERE {where}
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadAsync(
        Guid userId,
        NotificationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var unread = new NotificationFilter
        {
            IncludeAllHosts = filter.IncludeAllHosts,
            VisibleHostIds = filter.VisibleHostIds,
            UnreadOnly = true,
            Severity = filter.Severity,
            SourceKind = filter.SourceKind,
            Search = filter.Search,
            From = filter.From,
            To = filter.To,
        };

        return await this.CountAsync(userId, unread, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> MarkReadAsync(
        Guid userId,
        IReadOnlyCollection<Guid>? ids,
        DateTime readAt,
        NotificationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (ids is { Count: 0 })
        {
            return 0;
        }

        var (where, parameters) = BuildWhere(filter, includeDismissed: false);
        parameters.Add("UserId", userId.ToStorageText());
        parameters.Add("Now", Format(readAt));

        if (ids is not null)
        {
            where += " AND n.id IN @Ids";
            parameters.Add("Ids", ids.Select(id => id.ToStorageText()).ToArray());
        }

        var sql = $"""
            INSERT INTO notification_user_states (notification_id, user_id, read_at, dismissed_at)
            SELECT n.id, @UserId, @Now, NULL
            FROM notifications n
            LEFT JOIN notification_user_states s
                   ON s.notification_id = n.id AND s.user_id = @UserId
            WHERE {where}
            ON CONFLICT (notification_id, user_id) DO UPDATE
                SET read_at = COALESCE(notification_user_states.read_at, excluded.read_at)
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DismissAsync(
        Guid userId,
        IReadOnlyCollection<Guid>? ids,
        DateTime dismissedAt,
        NotificationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (ids is { Count: 0 })
        {
            return 0;
        }

        var (where, parameters) = BuildWhere(filter, includeDismissed: false);
        parameters.Add("UserId", userId.ToStorageText());
        parameters.Add("Now", Format(dismissedAt));

        if (ids is not null)
        {
            where += " AND n.id IN @Ids";
            parameters.Add("Ids", ids.Select(id => id.ToStorageText()).ToArray());
        }

        var sql = $"""
            INSERT INTO notification_user_states (notification_id, user_id, read_at, dismissed_at)
            SELECT n.id, @UserId, @Now, @Now
            FROM notifications n
            LEFT JOIN notification_user_states s
                   ON s.notification_id = n.id AND s.user_id = @UserId
            WHERE {where}
            ON CONFLICT (notification_id, user_id) DO UPDATE
                SET dismissed_at = excluded.dismissed_at,
                    read_at = COALESCE(notification_user_states.read_at, excluded.read_at)
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> TrimAsync(DateTime beforeUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var parameters = new { Before = Format(beforeUtc) };

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM notification_user_states
            WHERE notification_id IN (SELECT id FROM notifications WHERE occurred_at < @Before)
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var removed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notifications WHERE occurred_at < @Before",
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return removed;
    }

    /// <summary>
    /// 构造可见性 + 筛选项的 WHERE 片段。
    /// </summary>
    private static (string Where, DynamicParameters Parameters) BuildWhere(
        NotificationFilter filter,
        bool includeDismissed)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();

        if (!filter.IncludeAllHosts)
        {
            if (filter.VisibleHostIds.Count == 0)
            {
                clauses.Add("n.host_id IS NULL");
            }
            else
            {
                clauses.Add("(n.host_id IS NULL OR n.host_id IN @VisibleHostIds)");
                parameters.Add(
                    "VisibleHostIds",
                    filter.VisibleHostIds.Select(id => id.ToStorageText()).ToArray());
            }
        }

        if (!includeDismissed)
        {
            clauses.Add("s.dismissed_at IS NULL");
        }

        if (filter.UnreadOnly == true)
        {
            clauses.Add("s.read_at IS NULL");
        }

        if (filter.Severity is { } severity)
        {
            clauses.Add("n.severity = @Severity");
            parameters.Add("Severity", severity.ToStorageText());
        }

        if (filter.SourceKind is { } sourceKind)
        {
            clauses.Add("n.source_kind = @SourceKind");
            parameters.Add("SourceKind", sourceKind.ToString());
        }

        if (!String.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add(
                "(n.message_key LIKE @Search OR n.host_name LIKE @Search OR n.params_json LIKE @Search)");
            parameters.Add("Search", $"%{EscapeLike(filter.Search.Trim())}%");
        }

        if (filter.From is { } from)
        {
            clauses.Add("n.occurred_at >= @From");
            parameters.Add("From", Format(from));
        }

        if (filter.To is { } to)
        {
            clauses.Add("n.occurred_at <= @To");
            parameters.Add("To", Format(to));
        }

        return (clauses.Count == 0 ? "1 = 1" : String.Join(" AND ", clauses), parameters);
    }

    /// <summary>
    /// 转义 LIKE 通配符（与审计仓储同口径）。
    /// </summary>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// 统一时间格式。
    /// </summary>
    private static string Format(DateTime value) =>
        value.ToString(_timeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 查询行（通知本体 + 当前用户状态）。
    /// </summary>
    private sealed class NotificationRow
    {
        public Guid Id { get; set; }

        public NotificationSourceKind SourceKind { get; set; }

        public AlertSeverity Severity { get; set; }

        public string MessageKey { get; set; } = String.Empty;

        public string ParamsJson { get; set; } = "{}";

        public Guid? HostId { get; set; }

        public string? HostName { get; set; }

        public string? DedupeKey { get; set; }

        public DateTime OccurredAt { get; set; }

        public DateTime? ReadAt { get; set; }

        public DateTime? DismissedAt { get; set; }

        public Notification ToNotification() => Notification.Create(
            this.Id,
            this.SourceKind,
            this.Severity,
            this.MessageKey,
            this.ParamsJson,
            this.HostId,
            this.HostName,
            this.DedupeKey,
            this.OccurredAt);
    }
}
