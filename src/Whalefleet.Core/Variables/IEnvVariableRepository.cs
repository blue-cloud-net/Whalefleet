namespace Whalefleet.Core.Variables;

/// <summary>
/// 环境变量仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>CRUD 与全量列表见 <see cref="Common.IRepository{TEntity, TId}"/></b>：
/// <c>QueryByIdAsync</c> / <c>QueryAllListAsync</c> / <c>AddAsync</c> / <c>UpdateAsync</c> /
/// <c>RemoveByIdAsync</c> 由基接口统一声明。</para>
/// <para><b>位置</b>：接口在 <c>Core</c>，实现在 <c>Infrastructure/Persistence/Variables</c>。</para>
/// <para><b>「未命中」返回 <see langword="null"/> 而不是抛异常</b>：
/// 「这个变量不存在」要报哪个错误码（<c>1913</c>）由应用服务决定，
/// 仓储只负责如实回答「没有这一行」。</para>
/// </remarks>
public interface IEnvVariableRepository : Common.IRepository<EnvVariable, Guid>
{
    /// <summary>
    /// 按「作用域 + 名称」查询。
    /// </summary>
    /// <param name="hostId">所属主机主键；<see langword="null"/> 表示查全局变量。</param>
    /// <param name="composeName">所属 Compose 项目名；<see langword="null"/> 表示查全局变量。</param>
    /// <param name="name">变量名（区分大小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中的变量；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<EnvVariable?> FindByScopeAsync(
        Guid? hostId,
        string? composeName,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出全部全局变量。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全局变量列表（按名称升序）；没有数据时是空列表。</returns>
    Task<IReadOnlyList<EnvVariable>> ListGlobalAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某个 Compose 项目自己的变量。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="composeName">所属 Compose 项目名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>项目级变量列表（按名称升序）；没有数据时是空列表。</returns>
    Task<IReadOnlyList<EnvVariable>> ListByProjectAsync(
        Guid hostId,
        string composeName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某个 Compose 项目**可见**的全部变量（项目级 + 全局级）。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="composeName">所属 Compose 项目名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>变量列表（项目级在前，全局级在后，均按名称升序）。</returns>
    /// <remarks>
    /// <b>「可见」= 项目级与全局级的并集</b>：项目级同名时覆盖全局级，
    /// 因此调用方拿到列表后要按「同名取项目级」处理。本方法刻意<b>不</b>去重，
    /// 因为界面需要显示「这个变量被项目级覆盖了」。
    /// </remarks>
    Task<IReadOnlyList<EnvVariable>> ListVisibleAsync(
        Guid hostId,
        string composeName,
        CancellationToken cancellationToken = default);
}
