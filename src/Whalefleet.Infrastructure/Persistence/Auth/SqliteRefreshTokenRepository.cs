using Dapper;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Core.Auth;

namespace Whalefleet.Infrastructure.Persistence.Auth;

/// <summary>
/// 刷新令牌仓储的 SQLite / Dapper 实现。
/// </summary>
/// <remarks>
/// <para><b>无软删除</b>：本表不带 <c>is_deleted</c>，因此查询<b>不</b>追加软删除过滤；
/// 撤销靠 <c>revoked_at</c>，过期记录由 <see cref="DeleteExpiredAsync"/> 物理删除。</para>
/// <para><b>不接触令牌原文</b>：只按哈希读写。</para>
/// </remarks>
internal sealed class SqliteRefreshTokenRepository : RepositoryBase<RefreshToken, Guid>, IRefreshTokenRepository
{
    private const string _columns =
        "id, user_id, token_hash, expires_at, revoked_at, replaced_by_token_hash, creation_time, creator_id";

    private static readonly string _selectByHashSql = $"""
        SELECT {_columns}
        FROM refresh_tokens
        WHERE token_hash = @TokenHash
        """;

    private static readonly string _insertSql = $"""
        INSERT INTO refresh_tokens ({_columns})
        VALUES (@Id, @UserId, @TokenHash, @ExpiresAt, @RevokedAt, @ReplacedByTokenHash,
                @CreationTime, @CreatorId)
        """;

    private static readonly string _selectByIdSql = $"""
        SELECT {_columns}
        FROM refresh_tokens
        WHERE id = @Id
        """;

    // 本表无 is_deleted 列，全量列表因此不带软删除过滤（见类注释）
    private static readonly string _queryAllSql = $"""
        SELECT {_columns}
        FROM refresh_tokens
        ORDER BY creation_time
        """;

    private static readonly string _deleteByIdSql = """
        DELETE FROM refresh_tokens
        WHERE id = @Id
        """;

    private static readonly string _updateSql = """
        UPDATE refresh_tokens
        SET revoked_at = @RevokedAt,
            replaced_by_token_hash = @ReplacedByTokenHash
        WHERE id = @Id
        """;

    private static readonly string _revokeAllForUserSql = """
        UPDATE refresh_tokens
        SET revoked_at = @RevokedAt
        WHERE user_id = @UserId AND revoked_at IS NULL
        """;

    private static readonly string _deleteExpiredSql = """
        DELETE FROM refresh_tokens
        WHERE expires_at < @UtcNow
        """;

    /// <summary>
    /// 初始化仓储。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="timeProvider">时间源。</param>
    public SqliteRefreshTokenRepository(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
        : base(connections, currentUser, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override Task<int> ExecuteInsertAsync(
        System.Data.Common.DbConnection connection,
        RefreshToken token,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(_insertSql, ToParameters(token), cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override Task<RefreshToken?> QueryByIdAsync(
        System.Data.Common.DbConnection connection,
        Guid id,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<RefreshToken>(
            new CommandDefinition(_selectByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken));

    /// <inheritdoc />
    protected override string QueryAllSql => _queryAllSql;

    /// <inheritdoc />
    /// <remarks>
    /// <b>覆写为物理删除</b>：刷新令牌不参与软删除（撤销靠 <c>revoked_at</c>，
    /// 过期由 <see cref="DeleteExpiredAsync"/> 清理），因此基类的"软删除模板"在这里是错的。
    /// </remarks>
    public override async Task<bool> RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(_deleteByIdSql, new { Id = id.ToStorageText() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<RefreshToken?> FindByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RefreshToken>(
            new CommandDefinition(_selectByHashSql, new { TokenHash = tokenHash }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<bool> ExecuteUpdateAsync(
        System.Data.Common.DbConnection connection,
        RefreshToken token,
        CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                _updateSql,
                new { Id = token.Id.ToStorageText(), token.RevokedAt, token.ReplacedByTokenHash },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllForUserAsync(
        Guid userId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                _revokeAllForUserSql,
                new { UserId = userId.ToStorageText(), RevokedAt = utcNow },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(_deleteExpiredSql, new { UtcNow = utcNow }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static object ToParameters(RefreshToken token) => new
    {
        // 写入侧显式转文本：Dapper 的参数绑定会绕过类型处理器（值类型直接装箱），
        // 不转的话主键会按驱动默认的大写形态落库，与文档 / 运维脚本的小写字面量对不上。
        Id = token.Id.ToStorageText(),
        UserId = token.UserId.ToStorageText(),
        token.TokenHash,
        token.ExpiresAt,
        token.RevokedAt,
        token.ReplacedByTokenHash,
        token.CreationTime,
        CreatorId = token.CreatorId.ToStorageText(),
    };
}
