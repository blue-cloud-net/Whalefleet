using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;

namespace Whalefleet.IntegrationTests;

/// <summary>
/// 测试用的账号与角色分配辅助。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：0.1.0 起用户与角色是<b>多对多且带作用域</b>的关系，
/// 裸 <c>User.Create(...)</c> 建出来的账号<b>没有任何权限</b>——
/// 授权测试里它会通不过任何一个端点，而这与「账号是有效的」是两件事。
/// 让每个测试类各写一遍「建用户 + 建分配」只会让这段样板在十几个文件里各漂移一次。</para>
/// <para><b>刻意做成「建用户 + 建全局分配」</b>而不是提供
/// <c>CreateUserWithoutRole</c> 之类的开关：绝大多数端点测试真正需要的是
/// 「一个能登录、且具备某角色」的账号，而需要验证「无任何分配」的场景
/// 应当显式调用 <see cref="CreateUserOnly"/>，让这个意图出现在测试代码里。</para>
/// </remarks>
internal static class TestAccounts
{
    /// <summary>
    /// 建一个用户，并给它一条指定角色的**全局分配**。
    /// </summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="assignments">分配仓储。</param>
    /// <param name="username">用户名。</param>
    /// <param name="passwordHash">已哈希的密码。</param>
    /// <param name="role">要分配的角色（<see langword="null"/> 表示不分配任何角色）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的用户主键。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="users"/> 或 <paramref name="assignments"/> 为 <see langword="null"/>。</exception>
    public static async Task<Guid> CreateUserAsync(
        IUserRepository users,
        IUserRoleAssignmentRepository assignments,
        string username,
        string passwordHash,
        Role? role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(assignments);

        var user = User.Create(Guid.NewGuid(), username, passwordHash);
        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        if (role is not null)
        {
            await assignments
                .AddAsync(
                    UserRoleAssignment.Assign(Guid.NewGuid(), user.Id, role.Id),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return user.Id;
    }

    /// <summary>
    /// 建一个<b>没有任何角色分配</b>的用户。
    /// </summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="username">用户名。</param>
    /// <param name="passwordHash">已哈希的密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的用户主键。</returns>
    /// <remarks>
    /// 这是「新建用户到被分配角色之间」的正常状态：能登录，但看不到任何主机、
    /// 也过不了任何管理端点的策略。
    /// </remarks>
    public static async Task<Guid> CreateUserOnlyAsync(
        IUserRepository users,
        string username,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);

        var user = User.Create(Guid.NewGuid(), username, passwordHash);
        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        return user.Id;
    }

    /// <summary>
    /// 建一个用户，并给它一条**主机作用域**的分配（<paramref name="hostId"/> 非空）。
    /// </summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="assignments">分配仓储。</param>
    /// <param name="username">用户名。</param>
    /// <param name="passwordHash">已哈希的密码。</param>
    /// <param name="role">要分配的角色。</param>
    /// <param name="hostId">作用域主机主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的用户主键。</returns>
    /// <remarks>
    /// 这条路径是「在 A 主机能写、在 B 主机只读」的构造基础——
    /// 同一个角色分配两次、作用域不同。
    /// </remarks>
    public static async Task<Guid> CreateUserOnHostAsync(
        IUserRepository users,
        IUserRoleAssignmentRepository assignments,
        string username,
        string passwordHash,
        Role role,
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(role);

        var user = User.Create(Guid.NewGuid(), username, passwordHash);
        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        await assignments
            .AddAsync(
                UserRoleAssignment.Assign(Guid.NewGuid(), user.Id, role.Id, hostId),
                cancellationToken)
            .ConfigureAwait(false);

        return user.Id;
    }
}
