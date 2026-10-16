namespace Whalefleet.Application.Abstractions;

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
/// <para><b>角色是展示信息，不是授权事实</b>：多角色后 <see cref="Roles"/> 可能返回若干个名字，
/// 它们来自令牌里的 claim，<b>不参与任何授权判定</b>。
/// 带作用域的分配根本装不进单个 claim（见 <see cref="IPermissionChecker"/>），
/// 授权一律查库，因此令牌里的角色名只用于界面展示与菜单显隐。</para>
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
    /// 当前用户的角色名集合（可含多个）；未认证为空集合。
    /// </summary>
    /// <remarks>
    /// <b>为什么从单值变集合</b>：用户 ↔ 角色是多对多，把它们塞进一个 <c>role</c> claim
    /// 只能保住第一个，其余静默丢失。<b>刻意不返回 null</b>——
    /// 「没有角色」是正常状态（新建用户尚未分配任何角色），空集合比 null 少一次判空。
    /// </remarks>
    IReadOnlyList<string> Roles { get; }
}
