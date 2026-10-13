using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// 资源级权限校验（**接口先定，实现排 0.1.0**）。
/// </summary>
/// <remarks>
/// <para><b>与角色（<c>roles</c> 表）的分工</b>：角色回答「这个人是不是管理员」这类**全局**问题，
/// 由 <c>[Authorize(Roles = …)]</c> 在接口层解决；本接口回答的是**逐资源**的问题——
/// 「这个非管理员用户，能不能看到/操作<em>这一台</em>主机」。两者不是一回事，
/// 把后者塞进角色会立刻撞上「角色不可扩展」的问题。</para>
/// <para><b>为什么现在只定接口</b>：逐资源授权的数据基础（<c>user_host_permissions</c> /
/// <c>user_container_permissions</c>）排在 0.3.0 建表。此时实现出来只能是空实现或写死放行，
/// 两者都比"没有"更糟——前者会让调用方以为自己已经受保护了。</para>
/// <para><b>0.1.0 的落地方式</b>：实现 host 可见性（主机列表只返回可见主机、主机作用域端点校验可见性），
/// 容器 / 镜像 / Volume / Network 的细粒度校验随权限表一起在 0.3.0 补齐。</para>
/// </remarks>
public interface IResourcePermissionService
{
    /// <summary>
    /// 校验指定用户对指定主机是否可见；不可见时抛业务异常。
    /// </summary>
    /// <param name="userId">发起操作的用户主键。</param>
    /// <param name="host">目标主机。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="Common.BusinessException">
    /// 无权限访问该主机（错误码 <c>1501</c> <c>HostForbidden</c>）。
    /// </exception>
    /// <remarks>
    /// <b>用「抛异常」而不是「返回 bool」</b>：调用方拿到 <c>false</c> 时必须自己决定怎么办，
    /// 而实际只有一种正确处理——拒绝这次请求。返回布尔值等于把这个决定复制到每个调用点，
    /// 漏判一处就是一个越权入口。
    /// </remarks>
    Task EnsureHostVisibleAsync(Guid userId, DockerHost host, CancellationToken cancellationToken = default);
}
