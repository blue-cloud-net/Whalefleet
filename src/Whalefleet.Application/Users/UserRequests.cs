namespace Whalefleet.Application.Users;

/// <summary>
/// 用户管理端点的请求契约（列表 / 创建 / 删除 / 改密码）。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态。若声明为非空类型，「没传 username」会表现为
/// 空引用异常（500），而正确语义是参数校验失败（1000）。校验统一在 <see cref="UserService"/> 里做。
/// </remarks>
public sealed record ListUsersRequest
{
    /// <summary>页码（从 0 开始）；省略时按 0 处理。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数；省略时用默认值，超出上限会被夹紧。</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 创建用户的请求契约。
/// </summary>
public sealed record CreateUserRequest
{
    /// <summary>用户名。</summary>
    public string? Username { get; init; }

    /// <summary>明文密码（仅在本次请求内存中存在，绝不落库或写日志）。</summary>
    public string? Password { get; init; }

    /// <summary>
    /// 角色名；<b>省略时取默认角色</b>（当前为 <c>auditor</c>，可由管理员调整）。
    /// </summary>
    /// <remarks>
    /// 用角色名而不是 <c>roleId</c>：名字是稳定且可读的对外标识（自定义角色也由名字引用），
    /// 主键只在服务端内部流转。
    /// </remarks>
    public string? Role { get; init; }
}

/// <summary>
/// 删除用户的请求契约。
/// </summary>
/// <remarks>
/// <c>userId</c> 是**必填**的：删除不做"省略即删自己"这种默认值——
/// 省掉一个字段就删掉当前账号，这种默认值太危险。
/// </remarks>
public sealed record DeleteUserRequest
{
    /// <summary>目标用户主键。</summary>
    public Guid? UserId { get; init; }
}

/// <summary>
/// 修改密码的请求契约（管理员重置他人 / 用户改自己共用）。
/// </summary>
/// <remarks>
/// <para><c>userId</c> 省略或等于当前登录用户时视为**自助改密**，必须提供 <c>oldPassword</c>；
/// 指定为他人时为**管理员重置**，不需要原密码。</para>
/// <para>这样定规则的原因：自助改密若只凭一个被盗的 access token 就能改，攻击者可以把真正的
/// 主人锁在门外；而管理员重置本就是"替忘记密码的人恢复"的流程，要求原密码没有意义。</para>
/// </remarks>
public sealed record UpdatePasswordRequest
{
    /// <summary>目标用户主键；省略表示改自己的密码。</summary>
    public Guid? UserId { get; init; }

    /// <summary>原密码；自助改密时必填。</summary>
    public string? OldPassword { get; init; }

    /// <summary>新密码。</summary>
    public string? NewPassword { get; init; }
}
