using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 资源级权限校验（**接口先定，实现排 0.1.0**）。
/// </summary>
/// <remarks>
/// <para><b>与角色（<c>roles</c> 表）的分工</b>：角色回答「这个人是不是管理员」这类**全局**问题，
/// 由 <c>[Authorize(Roles = …)]</c> 在接口层解决；本接口回答的是**逐资源**的问题——
/// 「这个非管理员用户，能不能看到/操作<em>这一台</em>主机」。两者不是一回事，
/// 把后者塞进角色会立刻撞上「角色不可扩展」的问题。</para>
/// <para><b>当前的落地程度</b>：0.0.9 起已有实现（`Infrastructure/Permissions/ResourcePermissionService`），
/// 但策略只有两条——管理员放行、非管理员一律拒绝。逐资源授权的数据基础
/// （<c>user_host_permissions</c> / <c>user_container_permissions</c>）排在 0.1.0（主机）/ 0.5.0（容器）建表，
/// 在那之前「哪些主机对该用户可见」没有答案，只能 fail-closed。</para>
/// <para><b>调用点</b>：Api 的 <c>CurrentHostMiddleware</c> 在解析主机之后调用它，
/// 因此所有主机作用域端点自动受保护。容器 / 镜像 / Volume / Network 的细粒度校验随权限表一起在 0.1.0（主机）与 0.5.0（容器）补齐。</para>
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

    /// <summary>
    /// 判断指定用户对指定主机是否可见（<b>不</b>抛异常）。
    /// </summary>
    /// <param name="userId">发起操作的用户主键。</param>
    /// <param name="host">目标主机。</param>
    /// <returns>可见返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// <para><b>为什么在「保证可见」之外还要一个「能否可见」</b>：列表场景下没有「被拒绝的那个对象」——
    /// 要做的是一道过滤，把不可见的<em>滤掉</em>。若只提供抛异常的版本，调用方只能为每一台主机
    /// try/catch 一次，把正常的过滤写成异常流。</para>
    /// <para>两者的判定规则必须一致：<see cref="EnsureHostVisibleAsync"/> 的实现应复用本方法的结论，
    /// 否则会出现「列表里看不到、但直接访问又能进」这种最危险的偏差。</para>
    /// </remarks>
    bool IsHostVisible(Guid userId, DockerHost host);
}
