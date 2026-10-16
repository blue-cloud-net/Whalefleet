using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 权限判定：回答「当前用户**在某个作用域上**是否具备某项权限」。
/// </summary>
/// <remarks>
/// <para><b>为什么授权事实要查库而不是读 claim</b>：角色分配是**带作用域**的——
/// 同一人可能在 A 主机是 <c>operator</c>、在 B 主机是 <c>viewer</c>，
/// 因此没有任何一种单值 claim 能表达完整的授权事实。
/// 快照式 claim 还有一个更难察觉的代价：改了权限要等 token 过期才生效。</para>
/// <para><b>作用域由参数表达，而不是由「方法名」表达</b>：
/// <see cref="EnsurePermissionAsync(Guid, string?, string, CancellationToken)"/> 里
/// <c>hostId</c> 为 <see langword="null"/> 表示问的是<b>全局</b>权限。
/// 另开一个 <c>EnsureHostPermissionAsync</c> / <c>EnsureGlobalPermissionAsync</c> 看着更清楚，
/// 但那会让「作用域拼接 + 权限并集」这条规则被复制成两份实现，
/// 而规则一旦分叉就会出现「列表里看不到、但直接访问又能进」这种最危险的偏差。</para>
/// <para><b>实现位置</b>：接口在 <c>Application/Abstractions</c>，
/// 实现在 <c>Infrastructure</c>——它要读分配表与角色表，依赖仓储，就不属于应用层了。</para>
/// </remarks>
public interface IPermissionChecker
{
    /// <summary>
    /// 校验指定用户在指定作用域上是否具备某项权限；不具备时抛业务异常。
    /// </summary>
    /// <param name="userId">发起操作的用户主键。</param>
    /// <param name="hostId">作用域主机主键；<see langword="null"/> 表示问全局权限。</param>
    /// <param name="permission">权限标识（<c>domain.level</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="Common.BusinessException">
    /// 无权限。<b>主机作用域</b>缺权按「看不到这台机器」报 <c>1501</c>（<c>HostForbidden</c>），
    /// <b>全局</b>或主机内具体操作缺权报 <c>1502</c>（<c>OperationForbidden</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么用「抛异常」而不是「返回 bool」</b>：调用方拿到 <c>false</c> 时必须自己决定怎么办，
    /// 而实际只有一种正确处理——拒绝这次请求。返回布尔值等于把这个决定复制到每个调用点，
    /// 漏判一处就是一个越权入口。
    /// </remarks>
    Task EnsurePermissionAsync(
        Guid userId,
        Guid? hostId,
        string permission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断指定用户在指定作用域上是否具备某项权限（不抛异常）。
    /// </summary>
    /// <param name="userId">发起操作的用户主键。</param>
    /// <param name="hostId">作用域主机主键；<see langword="null"/> 表示问全局权限。</param>
    /// <param name="permission">权限标识（<c>domain.level</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>具备返回 <see langword="true"/>。</returns>
    Task<bool> HasPermissionAsync(
        Guid userId,
        Guid? hostId,
        string permission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 求指定用户在指定作用域上的<b>有效权限集</b>（判定规则的单一实现）。
    /// </summary>
    /// <param name="userId">发起操作的用户主键。</param>
    /// <param name="hostId">作用域主机主键；<see langword="null"/> 表示只求全局权限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有效权限标识集合（无权限时为空集合，不是 <see langword="null"/>）。</returns>
    /// <remarks>
    /// <para><b>生效规则</b>（唯一一份，别处都转调它）：</para>
    /// <list type="bullet">
    ///   <item><description>全局分配（<c>host_id IS NULL</c>）贡献该角色的 <b>Global</b> 权限；</description></item>
    ///   <item><description>主机分配贡献该角色的 <b>Host</b> 域权限，<b>且只在该主机上</b>，
    ///   它的 Global 权限被<b>忽略</b>——否则「把 admin 分配到某台主机」就成了系统级提权；</description></item>
    ///   <item><description>多条分配的权限取<b>并集</b>（同一人可同时是某主机的 operator 与 viewer）。</description></item>
    /// </list>
    /// <para>管理员（全局持有内置 <c>admin</c> 角色）短路返回全部权限，不查角色表。</para>
    /// <para><b>性能</b>：一次查询拿全该用户的分配，一次查询拿全相关角色的权限；
    /// 请求内缓存由实现负责（Scoped 服务 + 惰性求值），因此「主机列表过滤」不会变成 N 次往返。</para>
    /// </remarks>
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从一批主机中筛出指定用户<b>可见</b>的那些（是否具备 <c>host.read</c>）。
    /// </summary>
    /// <param name="userId">发起操作的用户主键。</param>
    /// <param name="hosts">候选主机（通常是全量）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可见的主机，顺序与 <paramref name="hosts"/> 一致；没有可见项时是<b>空列表</b>。</returns>
    /// <remarks>
    /// <para><b>为什么是「一次查库再内存过滤」而不是逐台判定</b>：可见性的输入只有「这个用户有哪些分配」，
    /// 一次查询就能得到答案；逐台判定会把 N 台主机变成 N 次串行 IO。
    /// 两处各判一次还有更坏的后果——规则一旦写歪，就会出现「列表里看不到、但直接访问又能进」。</para>
    /// <para>管理员短路时<b>不查库</b>：管理员的语义本来就是「全部可见」。</para>
    /// </remarks>
    Task<IReadOnlyList<DockerHost>> FilterVisibleHostsAsync(
        Guid userId,
        IReadOnlyList<DockerHost> hosts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前请求的用户是否为系统管理员（全局持有内置 <c>admin</c> 角色）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是管理员返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 只认<b>全局</b>分配：主机分配里的 <c>admin</c> 只带来该主机的 Host 域权限
    /// （即 <c>host.read</c> + <c>host.admin</c>），不是系统管理员。
    /// </remarks>
    Task<bool> IsAdministratorAsync(CancellationToken cancellationToken = default);
}
