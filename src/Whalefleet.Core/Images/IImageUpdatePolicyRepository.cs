using Whalefleet.Core.Common;

namespace Whalefleet.Core.Images;

/// <summary>
/// 镜像自动更新策略仓储。
/// </summary>
/// <remarks>
/// <b>「未命中」返回 <see langword="null"/> / <see langword="false"/>，不抛异常</b>：
/// 错误码（<c>1405</c> / <c>1406</c>）由应用服务决定。
/// </remarks>
public interface IImageUpdatePolicyRepository : IRepository<ImageUpdatePolicy, Guid>
{
    /// <summary>
    /// 列出某台主机的全部策略。
    /// </summary>
    /// <param name="hostId">主机主键（<c>hosts.id</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>策略列表（按镜像引用升序）；没有数据时是空列表。</returns>
    Task<IReadOnlyList<ImageUpdatePolicy>> ListByHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出全部<b>已启用</b>的策略（供后台扫描用）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>策略列表；没有数据时是空列表。</returns>
    Task<IReadOnlyList<ImageUpdatePolicy>> ListEnabledAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按「主机 + 镜像引用」查一条策略。
    /// </summary>
    /// <param name="hostId">主机主键（<c>hosts.id</c>）。</param>
    /// <param name="reference">镜像引用（<c>仓库:标签</c>，已规范化）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>策略；不存在时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>引用比较区分大小写</b>：仓库名在 OCI 规范里是小写，但标签是大小写敏感的
    /// （<c>v1.0</c> 与 <c>V1.0</c> 是两个标签），合并两者会让不同的策略互相覆盖。
    /// </remarks>
    Task<ImageUpdatePolicy?> FindAsync(
        Guid hostId,
        string reference,
        CancellationToken cancellationToken = default);
}
