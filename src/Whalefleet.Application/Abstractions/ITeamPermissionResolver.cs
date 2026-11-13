namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 团队权限上限解析器（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>团队只收窄、不扩权</b>：成员在某作用域上的有效权限 =
/// 个人权限 ∩ 团队权限上限。团队授权本身<b>不是</b>授权来源——
/// 成员的真正授权仍在 <c>user_role_assignments</c>。</para>
/// <para><b>为什么不让团队成为授权主体</b>：团队成员变更往往由 HR / 组长这类
/// <b>非管理员</b>操作。若「加进团队 = 获得权限」，那会变成一条绕过 <c>user.write</c>
/// 的提权路径，而团队管理端点的权限（<c>team.write</c>）与它实际能做的事不相称。</para>
/// <para><b>作用域规则</b>与个人授权一致：全局 / 主机 / 容器三级；
/// 团队在某个作用域上没授权时，该作用域<b>不收窄</b>（继承个人权限）——
/// 否则「加入团队」会让人平白失去已有的访问权。</para>
/// </remarks>
public interface ITeamPermissionResolver
{
    /// <summary>
    /// 求某用户在某作用域上的团队权限上限。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="hostId">作用域主机主键；<see langword="null"/> 表示全局作用域。</param>
    /// <param name="containerId">容器完整 ID；<see langword="null"/> 表示非容器级作用域。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 团队权限上限集合；<b><see langword="null"/> 表示该作用域没有团队约束</b>
    /// （用户不属于任何有该作用域授权的团队，或他是内置全局管理员）。
    /// </returns>
    Task<IReadOnlySet<string>?> GetCeilingAsync(
        Guid userId,
        Guid? hostId,
        string? containerId,
        CancellationToken cancellationToken = default);
}
