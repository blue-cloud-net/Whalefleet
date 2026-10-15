using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Users;

/// <summary>
/// 用户角色。
/// </summary>
/// <remarks>
/// <para><b>角色是数据，不是枚举</b>：角色存放在 <c>roles</c> 表，管理员可在运行时增删改，
/// 新增角色不需要改代码、重新发版。原先的 <c>UserRole</c> 枚举已随之删除——
/// 枚举会把"有哪些角色"这个业务事实焊死在编译期。</para>
/// <para><b>三个正交字段</b>：</para>
/// <list type="bullet">
///   <item><description><b>可读名称与描述</b>——面向用户展示（"运维：可操作已授权主机上的容器…"），
///   角色多了以后名字与说明比标识更重要；</description></item>
///   <item><description><b>内置角色</b>（<see cref="IsBuiltIn"/>）——随系统发布、<b>不可删除</b>；
///   其中 <c>admin</c> 是管理入口的依托（<c>[Authorize(Roles = "admin")]</c>），删掉会让系统失去管理员；</description></item>
///   <item><description><b>默认角色</b>（<see cref="IsDefault"/>）——创建用户未指定角色时采用；
///   全库<b>至多一个</b>，改默认由仓储在一个事务里完成。</description></item>
/// </list>
/// <para><b>角色不编码资源范围</b>：角色只是"身份标签"，
/// "某个用户能碰哪些主机 / 容器"由 0.1.0 落地的可见性表与 0.5.0 的容器级权限表（<c>user_host_permissions</c> 等）承担。
/// 因此不会出现"新建一个角色就等于新增一套权限"这种隐性授权。</para>
/// <para><b>软删除</b>：角色被删除后其历史授权记录仍在（不级联删用户）；角色名可再次使用
/// （唯一索引带 <c>WHERE is_deleted = 0</c>）。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class Role : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 内置管理员角色名。
    /// </summary>
    /// <remarks>
    /// 它是<b>唯一</b>的管理入口依托：<c>[Authorize(Roles = Role.AdminName)]</c> 与初始管理员引导
    /// 都引用这里，避免同一个字符串在多处各写一遍而漂移。该角色由 <c>is_built_in</c> 标记为不可删。
    /// </remarks>
    public const string AdminName = "admin";

    /// <summary>
    /// 角色名最小长度（去首尾空白后）。
    /// </summary>
    public const int MinNameLength = 2;

    /// <summary>
    /// 角色名最大长度（去首尾空白后）。角色名会进 JWT claim 与
    /// <c>[Authorize(Roles = …)]</c>，因此限制字符集并保持短小。
    /// </summary>
    public const int MaxNameLength = 32;

    /// <summary>
    /// 可读名称最大长度。
    /// </summary>
    public const int MaxDisplayNameLength = 64;

    /// <summary>
    /// 职责描述最大长度。
    /// </summary>
    public const int MaxDescriptionLength = 256;

    /// <summary>
    /// 角色标识（规范化小写，唯一且不区分大小写）。
    /// </summary>
    /// <remarks>
    /// 字符集限定为 ASCII 字母、数字与 <c>. _ -</c>：它会出现在 URL 路径、JSON 与 JWT claim 里。
    /// </remarks>
    public string Name { get; private set; } = String.Empty;

    /// <summary>
    /// 可读名称（面向用户展示，如"运维"）。允许为空，为空时界面回退显示 <see cref="Name"/>。
    /// </summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// 职责描述（面向用户展示，如"可操作已授权主机上的容器与镜像"）。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 是否为内置角色（不可删除）。
    /// </summary>
    public bool IsBuiltIn { get; private set; }

    /// <summary>
    /// 是否为默认角色（创建用户未指定角色时采用）。全库至多一个。
    /// </summary>
    public bool IsDefault { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/> 或 <see cref="CreateBuiltIn"/>。
    /// </summary>
    private Role()
    {
    }

    /// <summary>
    /// 创建一个<b>自定义</b>角色（可删除）。
    /// </summary>
    /// <param name="id">角色主键。</param>
    /// <param name="name">角色标识；自动转为小写，长度 2-32，仅允许 ASCII 字母、数字与 <c>. _ -</c>。</param>
    /// <param name="displayName">可读名称；为空则回退使用 <paramref name="name"/>。</param>
    /// <param name="description">职责描述；可为空。</param>
    /// <returns>新建的自定义角色（<see cref="IsBuiltIn"/> 为 <see langword="false"/>）。</returns>
    /// <exception cref="ArgumentException">角色标识或可读名称不合法。</exception>
    public static Role Create(Guid id, string name, string? displayName = null, string? description = null) =>
        new()
        {
            Id = id,
            Name = NormalizeName(name),
            DisplayName = NormalizeOptionalText(displayName, MaxDisplayNameLength, nameof(displayName)),
            Description = NormalizeOptionalText(description, MaxDescriptionLength, nameof(description)),
            IsBuiltIn = false,
            IsDefault = false,
        };

    /// <summary>
    /// 创建一个<b>内置</b>角色（不可删除）。仅供迁移脚本种入与测试使用。
    /// </summary>
    /// <param name="id">角色主键。</param>
    /// <param name="name">角色标识。</param>
    /// <param name="displayName">可读名称。</param>
    /// <param name="description">职责描述。</param>
    /// <param name="isDefault">是否同时设为默认角色。</param>
    /// <returns>新建的内置角色（<see cref="IsBuiltIn"/> 为 <see langword="true"/>）。</returns>
    /// <exception cref="ArgumentException">角色标识或可读名称不合法。</exception>
    public static Role CreateBuiltIn(
        Guid id,
        string name,
        string? displayName = null,
        string? description = null,
        bool isDefault = false) =>
        new()
        {
            Id = id,
            Name = NormalizeName(name),
            DisplayName = NormalizeOptionalText(displayName, MaxDisplayNameLength, nameof(displayName)),
            Description = NormalizeOptionalText(description, MaxDescriptionLength, nameof(description)),
            IsBuiltIn = true,
            IsDefault = isDefault,
        };

    /// <summary>
    /// 修改可读名称与职责描述。<b>角色标识不可改</b>——它已被用户引用，也是 JWT claim 的内容。
    /// </summary>
    /// <param name="displayName">新的可读名称；为空则回退使用 <see cref="Name"/>。</param>
    /// <param name="description">新的职责描述；可为空。</param>
    /// <exception cref="ArgumentException">文本超长。</exception>
    public void ChangeDescription(string? displayName, string? description)
    {
        this.DisplayName = NormalizeOptionalText(displayName, MaxDisplayNameLength, nameof(displayName));
        this.Description = NormalizeOptionalText(description, MaxDescriptionLength, nameof(description));
    }

    /// <summary>
    /// 标为默认角色。<b>不保证全库唯一</b>——唯一的保证来自
    /// <c>IRoleRepository.SetDefaultAsync</c>（它在同一事务内先清掉旧的默认标记）。
    /// </summary>
    public void MakeDefault() => this.IsDefault = true;

    /// <summary>
    /// 取消默认标记。
    /// </summary>
    public void ClearDefault() => this.IsDefault = false;

    /// <summary>
    /// 软删除该角色。<b>内置角色拒绝删除</b>。
    /// </summary>
    /// <param name="utcNow">删除时间（UTC）。</param>
    /// <param name="deleterId">删除者标识；传 <c>default</c> 表示系统操作。</param>
    /// <exception cref="InvalidOperationException">试图删除内置角色。</exception>
    /// <remarks>
    /// 「至少保留一名管理员」是<b>跨行</b>规则（要数还有几个 admin 用户），不在实体里判定，
    /// 由应用服务在调用本方法前检查。
    /// </remarks>
    public void Retire(DateTime utcNow, Guid deleterId)
    {
        if (this.IsBuiltIn)
        {
            throw new InvalidOperationException($"内置角色 '{this.Name}' 不可删除");
        }

        this.Delete(utcNow, deleterId);
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim().ToLowerInvariant();
        if (trimmed.Length is < MinNameLength or > MaxNameLength)
        {
            throw new ArgumentException(
                $"角色名长度必须在 {MinNameLength}-{MaxNameLength} 个字符之间",
                nameof(name));
        }

        foreach (var character in trimmed)
        {
            if (!Char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    $"角色名含非法字符 '{character}'：只允许 ASCII 字母、数字与 . _ -",
                    nameof(name));
            }
        }

        return trimmed;
    }

    private static string? NormalizeOptionalText(string? text, int maxLength, string parameterName)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"长度不得超过 {maxLength} 个字符", parameterName);
        }

        return trimmed;
    }
}
