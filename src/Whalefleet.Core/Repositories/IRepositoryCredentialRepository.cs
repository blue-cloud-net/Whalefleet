namespace Whalefleet.Core.Repositories;

/// <summary>
/// 仓库配置仓储（持久化契约）。
/// </summary>
/// <remarks>
/// <para><b>按别名与按地址各查一次</b>：前者服务管理界面（「这些仓库分别叫什么」），
/// 后者服务推镜像（「这个地址该用哪条凭据」）。两者都返回软删除过滤后的行。</para>
/// <para><b>唯一性由数据库兜底</b>：别名与地址各有一个带 <c>WHERE is_deleted = 0</c> 的
/// 唯一索引，删除后可再次使用同名 / 同址。</para>
/// </remarks>
public interface IRepositoryCredentialRepository : Common.IRepository<RepositoryCredential, Guid>
{
    /// <summary>
    /// 按别名查询（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="name">别名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<RepositoryCredential?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按服务地址查询（<b>不区分大小写</b>）。
    /// </summary>
    /// <param name="serverAddress">服务地址；调用方应先经 <see cref="RepositoryServerAddress"/> 规范化。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置；不存在或已软删除时返回 <see langword="null"/>。</returns>
    Task<RepositoryCredential?> FindByServerAddressAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断别名是否已被占用（不区分大小写，忽略已软删除的配置）。
    /// </summary>
    /// <param name="name">别名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断服务地址是否已被占用（不区分大小写，忽略已软删除的配置）。
    /// </summary>
    /// <param name="serverAddress">服务地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <see langword="true"/>。</returns>
    Task<bool> ExistsByServerAddressAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);
}
