namespace Whalefleet.Core.Teams;

/// <summary>
/// 团队（Team）—— 一组用户的花名册，兼作他们的<b>权限上限</b>。
/// </summary>
/// <remarks>
/// <para><b>团队只收窄、不扩权</b>：给团队授角色不会让成员凭空获得权限，
/// 它只限定成员在已授权作用域内的权限上限。成员的<b>个人</b>授权仍写在
/// <c>user_role_assignments</c>，由管理员显式同步写入。</para>
/// <para><b>为什么不把团队做成授权主体</b>：团队成员变更往往由 HR / 组长这类
/// <b>非管理员</b>操作。若「加进团队 = 获得权限」，那会变成一条绕过
/// <c>user.write</c> 的提权路径——团队管理端点的权限（<c>team.write</c>）
/// 将与它实际能做的事不相称。</para>
/// <para><b>名称不区分大小写且唯一</b>（数据库部分唯一索引兜底）：
/// 同名团队会让成员列表里出现两条分不清的选项。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化。</para>
/// </remarks>
public sealed class Team : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 团队名最小长度（去首尾空白后）。
    /// </summary>
    public const int MinNameLength = 2;

    /// <summary>
    /// 团队名最大长度（去首尾空白后）。
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// 团队标识（管理员起的名字，如「SRE 组」）。
    /// </summary>
    public string Name { get; private set; } = String.Empty;

    /// <summary>
    /// 可读名称；可为空（为空时界面回退显示 <see cref="Name"/>）。
    /// </summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// 备注（可为空）。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private Team()
    {
    }

    /// <summary>
    /// 创建团队。
    /// </summary>
    /// <param name="id">团队主键。</param>
    /// <param name="name">团队标识；首尾空白会被去除，长度 2-64。</param>
    /// <param name="displayName">可读名称（可为空）。</param>
    /// <param name="description">备注（可为空）。</param>
    /// <returns>新建的团队实体。</returns>
    /// <exception cref="ArgumentException">团队名为空或长度越界。</exception>
    public static Team Create(Guid id, string name, string? displayName = null, string? description = null)
    {
        return new Team
        {
            Id = id,
            Name = NormalizeName(name),
            DisplayName = NormalizeOptional(displayName),
            Description = NormalizeOptional(description),
        };
    }

    /// <summary>
    /// 更新团队的可读名称与备注。
    /// </summary>
    /// <param name="displayName">可读名称（可为空）。</param>
    /// <param name="description">备注（可为空）。</param>
    public void Update(string? displayName, string? description)
    {
        this.DisplayName = NormalizeOptional(displayName);
        this.Description = NormalizeOptional(description);
    }

    /// <summary>
    /// 规范化团队标识。
    /// </summary>
    /// <param name="name">原始标识。</param>
    /// <returns>去空白后的标识。</returns>
    /// <exception cref="ArgumentException">标识为空或长度越界。</exception>
    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        if (trimmed.Length is < MinNameLength or > MaxNameLength)
        {
            throw new ArgumentException(
                $"团队名长度必须在 {MinNameLength}-{MaxNameLength} 个字符之间",
                nameof(name));
        }

        return trimmed;
    }

    /// <summary>
    /// 归一化可选文本：空白视为「没有」。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>去空白后的值；为空时返回 <see langword="null"/>。</returns>
    private static string? NormalizeOptional(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
