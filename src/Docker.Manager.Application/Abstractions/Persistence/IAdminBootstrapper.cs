namespace Docker.Manager.Application.Abstractions.Persistence;

/// <summary>
/// 初始管理员引导：在系统中还没有任何账号时，用运维提供的环境变量创建一个管理员。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个接口</b>：它需要密码哈希器，而"数据库初始化"（<see cref="IDatabaseInitializer"/>）
/// 不该因此依赖安全组件——否则任何只想建库的调用方（如持久化层的测试宿主）都必须先准备一个哈希器。</para>
/// <para><b>执行时机</b>：必须在 <see cref="IDatabaseInitializer.InitializeAsync"/> 之后——
/// 它要读 <c>users</c> / <c>roles</c> 表，而这两张表由架构迁移创建。</para>
/// <para><b>幂等</b>：只要 <c>users</c> 表存在任意一行（含已软删除行），本步骤立即返回。</para>
/// </remarks>
public interface IAdminBootstrapper
{
    /// <summary>
    /// 按需创建初始管理员。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="InvalidOperationException">
    /// 密码不满足强度要求，或内置 <c>admin</c> 角色缺失（迁移不完整）。
    /// </exception>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
