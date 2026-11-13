namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 实例级安全策略存储（0.13.0）：密码强度与有效期、首次登录强制改密。
/// </summary>
/// <remarks>
/// <para><b>为什么是抽象</b>：设置存在 SQLite 的 <c>security_settings</c> 单行表里，
/// 而应用层不应知道 SQL 与表结构。与 <c>IAuthorizationSettingsStore</c> 同口径。</para>
/// <para><b>为什么放在 <c>Application/Abstractions</c> 而非 <c>Core</c></c>：
/// 它是一个**外部资源**（持久化设置）的访问口，而不是业务概念。
/// 业务概念（密码策略本身）在 <c>Application/Security/PasswordPolicyOptions</c>。</para>
/// </remarks>
public interface ISecuritySettingsStore
{
    /// <summary>
    /// 读取当前安全策略。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>安全策略；表里没有记录时返回<b>默认值</b>（而不是 null）。</returns>
    Task<SecurityPolicyOptions> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存安全策略。
    /// </summary>
    /// <param name="options">待保存的策略（调用方负责校验合法性）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task SetAsync(SecurityPolicyOptions options, CancellationToken cancellationToken = default);
}
