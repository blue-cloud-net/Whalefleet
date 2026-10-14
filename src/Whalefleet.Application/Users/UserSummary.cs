namespace Whalefleet.Application.Users;

/// <summary>
/// 用户列表项（管理端点的返回契约）。
/// </summary>
/// <param name="Id">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="Role">角色名（如 <c>admin</c>）。</param>
/// <param name="CreationTime">创建时间（UTC）。</param>
/// <remarks>
/// <para><b>为什么不用实体直接序列化</b>：<c>User</c> 带 <c>PasswordHash</c>。
/// 逐字段挑选不是洁癖——把实体丢给序列化器，任何一个新加的敏感字段都会自动出现在响应里。</para>
/// <para>刻意<b>不</b>返回手机号 / 邮箱之类的字段：本项目当前没有这些信息，不预留。</para>
/// </remarks>
public sealed record UserSummary(
    Guid Id,
    string Username,
    string Role,
    DateTimeOffset CreationTime);

/// <summary>
/// 分页结果。
/// </summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数（未分页时的全量）。</param>
/// <param name="PageIndex">当前页码（从 0 开始）。</param>
/// <param name="PageSize">每页条数（夹紧后的实际值）。</param>
/// <remarks>
/// 把夹紧后的 <see cref="PageIndex"/> / <see cref="PageSize"/> 一并返回：调用方传了
/// <c>pageSize=9999</c> 却只拿到 100 条时，能从响应里看出服务端实际用了多少，
/// 不必去猜是不是丢数据了。
/// </remarks>
public sealed record UserListResult(
    IReadOnlyList<UserSummary> Items,
    int Total,
    int PageIndex,
    int PageSize);
