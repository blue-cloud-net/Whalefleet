namespace Whalefleet.Application.Teams;

/// <summary>
/// 团队管理端点的请求契约。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：与项目其余请求一致，缺字段是常态，
/// 校验统一在 <see cref="TeamService"/> 里做（错误码 <c>1000</c>）。
/// </remarks>
public sealed record ListTeamsRequest
{
    /// <summary>页码（从 0 开始）。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数（省略时用默认值，超出上限会被夹紧）。</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 创建团队的请求契约。
/// </summary>
public sealed record CreateTeamRequest
{
    /// <summary>团队标识（必填）。</summary>
    public string? Name { get; init; }

    /// <summary>可读名称（可为空）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>备注（可为空）。</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 更新团队的请求契约。
/// </summary>
public sealed record UpdateTeamRequest
{
    /// <summary>目标团队主键（必填）。</summary>
    public Guid? TeamId { get; init; }

    /// <summary>可读名称（可为空）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>备注（可为空）。</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 删除团队的请求契约。
/// </summary>
public sealed record RemoveTeamRequest
{
    /// <summary>目标团队主键（必填）。</summary>
    public Guid? TeamId { get; init; }
}

/// <summary>
/// 调整团队成员的请求契约。
/// </summary>
public sealed record UpdateTeamMembersRequest
{
    /// <summary>目标团队主键（必填）。</summary>
    public Guid? TeamId { get; init; }

    /// <summary>用户主键集合（必填；可为空数组表示不动）。</summary>
    public IReadOnlyList<Guid>? UserIds { get; init; }
}

/// <summary>
/// 授予 / 撤销团队角色的请求契约。
/// </summary>
/// <remarks>
/// <para><b>作用域字段的形状与个人授权一致</b>：<c>host</c> 省略即全局；
/// <c>containerId</c> 必须同时带 <c>host</c>。</para>
/// <para><b>它表达的是「上限」不是「授权」</b>：给团队授角色只会<b>收窄</b>成员的权限，
/// 不会让成员凭空获得什么。真正的个人授权由「同步到成员」写入。</para>
/// </remarks>
public sealed record TeamRoleAssignmentRequest
{
    /// <summary>目标团队主键（必填）。</summary>
    public Guid? TeamId { get; init; }

    /// <summary>角色主键；<c>roleId</c> 与 <c>role</c> 至少提供一个。</summary>
    public Guid? RoleId { get; init; }

    /// <summary>角色标识（不区分大小写）；<c>roleId</c> 与 <c>role</c> 至少提供一个。</summary>
    public string? Role { get; init; }

    /// <summary>作用域主机名；省略或为空表示全局。</summary>
    public string? Host { get; init; }

    /// <summary>容器完整 ID；必须同时提供 <c>host</c>。</summary>
    public string? ContainerId { get; init; }
}

/// <summary>
/// 把团队权限同步到成员的请求契约。
/// </summary>
public sealed record SyncTeamRequest
{
    /// <summary>目标团队主键（必填）。</summary>
    public Guid? TeamId { get; init; }

    /// <summary>
    /// 要同步到的成员；<b>省略表示全部成员</b>。
    /// </summary>
    /// <remarks>
    /// 省略即全部是刻意的：典型场景是「把团队新授予的角色同步给所有人」，
    /// 而让管理员先手工列出成员 ID 列表既繁琐又容易漏。
    /// </remarks>
    public IReadOnlyList<Guid>? UserIds { get; init; }
}
