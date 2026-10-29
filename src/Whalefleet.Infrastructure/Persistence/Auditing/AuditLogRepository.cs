using System.Globalization;
using System.Text;
using Dapper;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Infrastructure.Persistence.Auditing;

/// <summary>
/// 审计日志仓储的 SQLite / Dapper 实现（<b>只追加 + 只查</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么不派生 <see cref="RepositoryBase{TEntity,TId}"/></b>：那个基类提供
/// 「软删除 + 更新 + 按主键删除」的模板，而审计表一条都不该有。硬套会把
/// 「可以被删掉」变成架构里一个随时可用的口子。</para>
/// <para><b>为什么时间显式格式化成文本</b>：库里的时间是 TEXT，范围过滤与排序都靠字符串比较。
/// 驱动默认可能写出<b>不带毫秒</b>的形态，而另一些写入带毫秒——两者混在一起时
/// <c>"...12:00:00" &lt; "...12:00:00.005"</c> 的字典序会把同一秒内的记录排反。
/// 统一成 <c>yyyy-MM-dd HH:mm:ss.fff</c> 后，字典序与时间序一致。</para>
/// <para><b>为什么 LIKE 关键字要转义</b>：用户搜 <c>100%</c> 时，未转义的 <c>%</c>
/// 会被当成通配符，于是「搜 100% 却返回一堆无关记录」——而界面看不出是通配符在起作用。</para>
/// </remarks>
internal sealed class AuditLogRepository : IAuditLogRepository
{
    /// <summary>
    /// 统一的时间存储格式（与查询过滤共用，保证字典序 = 时间序）。
    /// </summary>
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string _columns =
        "id, category, action, actor_id, actor_name, target_type, target_id, target_name, "
        + "host_id, host_name, outcome, error_code, client_ip, detail, occurred_at";

    private readonly IDbConnectionFactory _connections;

    /// <summary>
    /// 初始化审计仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <exception cref="ArgumentNullException"><paramref name="connections"/> 为 <see langword="null"/>。</exception>
    public AuditLogRepository(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async Task AddAsync(AuditLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        const string sql = """
            INSERT INTO audit_logs
                (id, category, action, actor_id, actor_name, target_type, target_id, target_name,
                 host_id, host_name, outcome, error_code, client_ip, detail, occurred_at)
            VALUES
                (@Id, @Category, @Action, @ActorId, @ActorName, @TargetType, @TargetId, @TargetName,
                 @HostId, @HostName, @Outcome, @ErrorCode, @ClientIp, @Detail, @OccurredAt)
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = log.Id.ToStorageText(),
                Category = log.Category.ToStorageText(),
                log.Action,

                // 主键一律显式转小写文本：Dapper 的参数绑定绕过 ITypeHandler（见 AGENTS.md 陷阱 2）。
                ActorId = log.ActorId?.ToStorageText(),
                log.ActorName,
                log.TargetType,
                log.TargetId,
                log.TargetName,
                HostId = log.HostId?.ToStorageText(),
                log.HostName,
                Outcome = log.Outcome.ToStorageText(),
                log.ErrorCode,
                log.ClientIp,
                log.Detail,
                OccurredAt = Format(log.OccurredAt),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLog>> ListAsync(
        AuditLogFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (where, parameters) = BuildWhere(filter);
        parameters.Add("Skip", offset);
        parameters.Add("Take", limit);

        var sql = $"""
            SELECT {_columns}
            FROM audit_logs
            WHERE {where}
            ORDER BY occurred_at DESC, id DESC
            LIMIT @Take OFFSET @Skip
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AuditLog>(new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (where, parameters) = BuildWhere(filter);

        var sql = $"SELECT COUNT(1) FROM audit_logs WHERE {where}";

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 拼装 WHERE 子句与参数。
    /// </summary>
    /// <param name="filter">过滤条件。</param>
    /// <returns>WHERE 片段（不含关键字）与对应参数。</returns>
    private static (string Where, DynamicParameters Parameters) BuildWhere(AuditLogFilter filter)
    {
        var where = new StringBuilder("category = @Category");
        var parameters = new DynamicParameters();

        parameters.Add("Category", filter.Category.ToStorageText());

        if (filter.From is { } from)
        {
            where.Append(" AND occurred_at >= @From");
            parameters.Add("From", Format(from));
        }

        if (filter.To is { } to)
        {
            where.Append(" AND occurred_at <= @To");
            parameters.Add("To", Format(to));
        }

        if (filter.ActorName is { } actor)
        {
            where.Append(" AND actor_name LIKE @Actor ESCAPE '\\'");
            parameters.Add("Actor", $"%{EscapeLike(actor)}%");
        }

        if (filter.Action is { } action)
        {
            where.Append(" AND action = @Action");
            parameters.Add("Action", action);
        }

        if (filter.TargetType is { } targetType)
        {
            where.Append(" AND target_type = @TargetType");
            parameters.Add("TargetType", targetType);
        }

        if (filter.Outcome is { } outcome)
        {
            where.Append(" AND outcome = @Outcome");
            parameters.Add("Outcome", outcome.ToStorageText());
        }

        if (filter.HostId is { } hostId)
        {
            where.Append(" AND host_id = @HostId");
            parameters.Add("HostId", hostId.ToStorageText());
        }

        if (filter.Search is { } search)
        {
            // 目标名 / 主机名 / 说明 / 操作者四处一起搜：界面只有一个搜索框，
            // 而用户心里想的是「那个 nginx 的记录」或「张三干的事」。
            where.Append(
                " AND (target_name LIKE @Search ESCAPE '\\'"
                + " OR host_name LIKE @Search ESCAPE '\\'"
                + " OR detail LIKE @Search ESCAPE '\\'"
                + " OR actor_name LIKE @Search ESCAPE '\\')");
            parameters.Add("Search", $"%{EscapeLike(search)}%");
        }

        return (where.ToString(), parameters);
    }

    /// <summary>
    /// 统一时间格式。
    /// </summary>
    private static string Format(DateTime value) =>
        value.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 转义 LIKE 的通配符。
    /// </summary>
    /// <param name="value">用户输入。</param>
    /// <returns>可安全放进 LIKE 模式的文本。</returns>
    /// <remarks>
    /// 顺序很重要：先转义反斜杠本身，否则后两步插入的反斜杠会被再次转义。
    /// </remarks>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
