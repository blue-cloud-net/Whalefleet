namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// 当前请求的登录用户。
/// </summary>
/// <remarks>
/// <para><b>用途</b>：仓储基类用它填充审计字段（<c>CreatorId</c> / <c>LastModifierId</c> / <c>DeleterId</c>）。</para>
/// <para><b>未认证时的语义</b>：<see cref="IsAuthenticated"/> 为 <see langword="false"/>、
/// <see cref="UserId"/> 为 <see langword="null"/>；审计字段按「默认值哨兵」写入
/// <see cref="Guid.Empty"/>，表示<b>系统操作</b>（如启动引导、后台维护任务）。</para>
/// <para><b>实现归属</b>：0.0.3（认证尚未落地）由 Infrastructure 的占位实现提供；
/// 0.0.4 认证落地后由 Api 层基于 <c>HttpContext</c> + JWT claims 的实现<b>替换</b>。</para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// 当前请求是否已通过认证。
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// 当前用户主键；未认证为 <see langword="null"/>。
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// 当前用户名；未认证为 <see langword="null"/>。
    /// </summary>
    string? Username { get; }

    /// <summary>
    /// 当前用户角色名（<c>admin</c> / <c>user</c>）；未认证为 <see langword="null"/>。
    /// </summary>
    string? Role { get; }
}
