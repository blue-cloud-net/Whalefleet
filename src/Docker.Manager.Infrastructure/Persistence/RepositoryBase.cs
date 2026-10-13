using System.Data.Common;
using Dapper;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Core.Common;
using Docker.Manager.Core.Common.Auditing;

namespace Docker.Manager.Infrastructure.Persistence;

/// <summary>
/// 仓储基类：统一提供连接管理、审计填充，以及 <see cref="IRepository{TEntity, TId}"/>
/// 四个 CRUD 与全量列表查询的<b>骨架</b>。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
/// <typeparam name="TId">实体主键类型。</typeparam>
/// <remarks>
/// <para><b>为什么抽象成模板方法</b>：CRUD 的<b>控制流</b>完全相同，差别只在 SQL 与参数映射：
/// 开连接 → 填充审计 → 执行 → 把受影响行数翻译成 <c>bool</c>。这些控制流原先在每个仓储里
/// 各写一遍，于是「忘了填审计」「忘了过滤软删除」「忘了判 <c>affected == 1</c>」
/// 都是很容易犯且很难发现的错误。现在只有一份，在基类里。</para>
/// <para><b>派生类只需提供四件事</b>：按主键查询、全量列表 SQL、单条插入、单条更新的 SQL 与参数绑定。
/// 其它方法（分页 / 计数 / 按业务键查询）留在具体仓储，不进基类。
/// <b>排序也不进基类</b>：各表"全量列表"的合理顺序本就不同（主机按创建时间、角色按内置优先），
/// 因此由派生类在 <see cref="QueryAllSql"/> 里各自给全。</para>
/// <para><b>审计由仓储填充</b>：调用方只管调实体方法改业务属性，创建 / 修改 / 删除审计
/// 由本类在写库前通过 <c>Mark*</c> 方法灌入，取值来自 <see cref="TimeProvider"/>
/// 与 <see cref="ICurrentUser"/>——实体自身<b>不</b>获取当前时间与当前用户。</para>
/// <para><b>为什么审计方法里用运行时类型判断而不是泛型约束</b>：<see cref="TEntity"/> 只能约束到
/// <see cref="Entity{TId}"/>（覆盖全部实体），而 <c>MarkCreated</c> / <c>MarkModified</c> / <c>Delete</c>
/// 分别定义在不同审计基类上。约束到具体基类会让基类无法服务"只带部分审计"的实体（如刷新令牌）；
/// 因此这里按实际支持的层级逐级判断，不支持的那级直接跳过。</para>
/// <para><b>删除语义由派生类决定</b>：默认模板走「软删除」（读出实体 → 打删除标记 → 走更新路径），
/// 删除与更新因此共用同一套审计填充与 SQL 钩子。
/// 不参与软删除的实体（如刷新令牌）需要<b>覆写</b> <see cref="RemoveByIdAsync"/> 为物理删除。</para>
/// <para><b>软删除过滤</b>：仓储生成的 <c>SELECT</c> / <c>UPDATE</c> / <c>DELETE</c> 必须追加
/// <see cref="SoftDeleteFilter"/>，让过滤下沉到 SQL（能走索引，且不会因某个查询忘了判断而泄漏已删除数据）。</para>
/// <para><b>连接生命周期</b>：每个方法内 <c>using</c> 一个连接，用完即还池；
/// 本基类不持有长命连接，也不提供跨仓储事务（0.0.3 的业务写入均为单语句）。</para>
/// </remarks>
internal abstract class RepositoryBase<TEntity, TId> : IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : notnull
{
    /// <summary>
    /// 软删除过滤片段：所有业务查询都必须追加它。
    /// </summary>
    protected const string SoftDeleteFilter = "is_deleted = 0";

    /// <summary>
    /// 初始化仓储基类。
    /// </summary>
    /// <param name="connections">数据库连接工厂。</param>
    /// <param name="currentUser">当前登录用户（用于审计）。</param>
    /// <param name="timeProvider">时间源（用于审计）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    protected RepositoryBase(
        IDbConnectionFactory connections,
        ICurrentUser currentUser,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Connections = connections;
        CurrentUser = currentUser;
        TimeProvider = timeProvider;
    }

    /// <summary>
    /// 数据库连接工厂。
    /// </summary>
    protected IDbConnectionFactory Connections { get; }

    /// <summary>
    /// 当前登录用户。
    /// </summary>
    protected ICurrentUser CurrentUser { get; }

    /// <summary>
    /// 时间源。
    /// </summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>
    /// 当前 UTC 时间（审计写入用）。
    /// </summary>
    protected DateTime UtcNow => TimeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// 当前用户主键；未认证时为 <see cref="Guid.Empty"/>（默认值哨兵，表示系统操作）。
    /// </summary>
    protected Guid CurrentUserId => CurrentUser.UserId ?? Guid.Empty;

    /// <inheritdoc />
    public virtual async Task<TEntity?> QueryByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await QueryByIdAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TEntity>> QueryAllListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var entities = await connection.QueryAsync<TEntity>(
            new CommandDefinition(QueryAllSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return entities.AsList();
    }

    /// <inheritdoc />
    public virtual async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        MarkCreated(entity);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteInsertAsync(connection, entity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<bool> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        MarkModified(entity);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ExecuteUpdateAsync(connection, entity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 默认模板是<b>软删除</b>：读出实体 → 打删除标记 → 走正常的更新路径。
    /// 不参与软删除的实体（无 <c>FullAuditedEntity</c> 基类）需要<b>覆写</b>本方法为物理删除。
    /// </remarks>
    public virtual async Task<bool> RemoveByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        var entity = await QueryByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        MarkDeleted(entity);

        return await UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开一个新连接。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的连接；调用方负责释放（返回 <see cref="DbConnection"/> 以便 <c>await using</c>）。</returns>
    protected async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = Connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// 按主键查询单条（<b>派生类必须提供</b>）。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="id">实体主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实体；不存在或已软删除时返回 <see langword="null"/>。</returns>
    protected abstract Task<TEntity?> QueryByIdAsync(
        DbConnection connection,
        TId id,
        CancellationToken cancellationToken);

    /// <summary>
    /// 全量列表查询 SQL（<b>派生类必须提供</b>）：投影列、软删除过滤与 <c>ORDER BY</c> 一并给全。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>由基类用「表名 + 列名 + 过滤 + 排序」拼装：<c>refresh_tokens</c> 没有
    /// <c>is_deleted</c> 列，基类无从判断该不该追加 <see cref="SoftDeleteFilter"/>；
    /// 而排序语义也各不一致。整段交出去是唯一不会在某个仓储上拼错的写法。
    /// </remarks>
    protected abstract string QueryAllSql { get; }

    /// <summary>
    /// 执行单条插入（<b>派生类必须提供</b>）。审计字段已由基类填充。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="entity">待插入的实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数（正常为 1）。</returns>
    protected abstract Task<int> ExecuteInsertAsync(
        DbConnection connection,
        TEntity entity,
        CancellationToken cancellationToken);

    /// <summary>
    /// 执行单条更新（<b>派生类必须提供</b>）。审计字段已由基类填充。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="entity">待更新的实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否命中一行（目标不存在或已软删除时为 <see langword="false"/>）。</returns>
    /// <remarks>实现里<b>必须</b>把行数翻译成 <c>true/false</c>（<c>affected == 1</c>），
    /// 否则基类无法把"未命中"如实传给调用方。</remarks>
    protected abstract Task<bool> ExecuteUpdateAsync(
        DbConnection connection,
        TEntity entity,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 Provider 中立的方式添加命令参数。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不用</b> <c>AddWithValue</c>：那是 <c>SqliteParameterCollection</c> 的扩展方法，
    /// 而连接来自 <c>IDbConnectionFactory</c>（返回 <see cref="DbConnection"/>）。用
    /// <see cref="DbCommand.CreateParameter"/> 可以让仓储不被 SQLite 的专有 API 绑死。
    /// </remarks>
    /// <param name="command">目标命令。</param>
    /// <param name="name">参数名（含前缀）。</param>
    /// <param name="value">参数值。</param>
    protected static void AddParameter(DbCommand command, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// 写入创建审计。实体若不支持该级审计则跳过。
    /// </summary>
    /// <param name="entity">实体。</param>
    protected void MarkCreated(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is CreationAuditedEntity<TId, Guid> audited)
        {
            audited.MarkCreated(UtcNow, CurrentUserId);
        }
    }

    /// <summary>
    /// 写入修改审计。实体若不支持该级审计则跳过。
    /// </summary>
    /// <param name="entity">实体。</param>
    protected void MarkModified(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is AuditedEntity<TId, Guid> audited)
        {
            audited.MarkModified(UtcNow, CurrentUserId);
        }
    }

    /// <summary>
    /// 写入删除审计（软删除）。实体若不支持软删除则跳过。
    /// </summary>
    /// <param name="entity">实体。</param>
    protected void MarkDeleted(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is FullAuditedEntity<TId, Guid> audited)
        {
            audited.Delete(UtcNow, CurrentUserId);
        }
    }
}