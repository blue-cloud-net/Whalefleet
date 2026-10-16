using Whalefleet.Core.Common.Auditing;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Core.Users;

/// <summary>
/// 用户角色。
/// </summary>
/// <remarks>
/// <para><b>角色是数据，不是枚举</b>：角色存放在 <c>roles</c> 表，管理员可在运行时增删改，
/// 新增角色不需要改代码、重新发版。原先的 <c>UserRole</c> 枚举已随之删除——
/// 枚举会把"有哪些角色"这个业务事实焊死在编译期。</para>
/// <para><b>角色携带权限（0.1.0）</b>：本实体带一个 <see cref="Permissions"/> 权限集，
/// 这正是「运维」能真正成为运维的原因——在此之前角色只是个身份标签，
/// 内置角色的职责描述不参与任何授权判定，一个被直接授予写权限的「只读」用户照样能写。
/// 权限集与「在哪生效」是两回事：后者由 <see cref="UserRoleAssignment"/> 承担。</para>
/// <para><b>三个正交字段</b>：</para>
/// <list type="bullet">
///   <item><description><b>可读名称与描述</b>——面向用户展示（"运维：可操作已授权主机上的容器…"），
///   角色多了以后名字与说明比标识更重要；</description></item>
///   <item><description><b>内置角色</b>（<see cref="IsBuiltIn"/>）——随系统发布、<b>不可删除</b>；
///   其中 <c>admin</c> 是管理入口的依托（<c>[Authorize(Roles = "admin")]</c>），删掉会让系统失去管理员；</description></item>
///   <item><description><b>默认角色</b>（<see cref="IsDefault"/>）——创建用户未指定角色时采用；
///   全库<b>至多一个</b>，改默认由仓储在一个事务里完成。</description></item>
/// </list>
/// <para><b>角色不编码资源范围</b>：角色回答的是「能做什么」，而「在哪台主机上生效」
/// 由 0.1.0 落地的 <c>user_role_assignments</c> 表（<see cref="HostId"/> 可空，NULL = 全局）
/// 承担。因此不会出现"新建一个角色就等于新增一套可见主机"这种隐性授权。</para>
/// <para><b>软删除</b>：角色被删除后其历史分配记录仍在（不级联删分配）；角色名可再次使用
/// （唯一索引带 <c>WHERE is_deleted = 0</c>）。删除前必须确认没有有效分配（应用服务负责）。</para>
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
    /// 角色携带的权限集（规范化后的小写标识，去重且有序）。
    /// </summary>
    /// <remarks>
    /// 存于 <c>role_permissions</c> 表，一个 (role_id, permission) 组合一行；
    /// 本属性是该表的物化投影。<b>不持久的唯一副本</b>——改权限走
    /// <see cref="SetPermissions"/>，仓储负责把差量落到表上。
    /// </remarks>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/> 或 <see cref="CreateBuiltIn"/>。
    /// </summary>
    private Role()
    {
    }

    /// <summary>
    /// 创建一个<b>自定义</b>角色（可删除、可改权限）。
    /// </summary>
    /// <param name="id">角色主键。</param>
    /// <param name="name">角色标识；自动转为小写，长度 2-32，仅允许 ASCII 字母、数字与 <c>. _ -</c>。</param>
    /// <param name="displayName">可读名称；为空则回退使用 <paramref name="name"/>。</param>
    /// <param name="description">职责描述；可为空。</param>
    /// <param name="permissions">初始权限集；不得含目录之外的权限。</param>
    /// <returns>新建的自定义角色（<see cref="IsBuiltIn"/> 为 <see langword="false"/>）。</returns>
    /// <exception cref="ArgumentException">角色标识、可读名称或权限不合法。</exception>
    public static Role Create(
        Guid id,
        string name,
        string? displayName = null,
        string? description = null,
        IEnumerable<string>? permissions = null) =>
        new()
        {
            Id = id,
            Name = NormalizeName(name),
            DisplayName = NormalizeOptionalText(displayName, MaxDisplayNameLength, nameof(displayName)),
            Description = NormalizeOptionalText(description, MaxDescriptionLength, nameof(description)),
            IsBuiltIn = false,
            IsDefault = false,
            Permissions = NormalizePermissions(permissions),
        };

    /// <summary>
    /// 创建一个<b>内置</b>角色（不可删除，权限不可修改）。仅供迁移脚本种入与测试使用。
    /// </summary>
    /// <param name="id">角色主键。</param>
    /// <param name="name">角色标识。</param>
    /// <param name="displayName">可读名称。</param>
    /// <param name="description">职责描述。</param>
    /// <param name="permissions">固定权限集；不得含目录之外的权限。</param>
    /// <param name="isDefault">是否同时设为默认角色。</param>
    /// <returns>新建的内置角色（<see cref="IsBuiltIn"/> 为 <see langword="true"/>）。</returns>
    /// <exception cref="ArgumentException">角色标识、可读名称或权限不合法。</exception>
    public static Role CreateBuiltIn(
        Guid id,
        string name,
        string? displayName = null,
        string? description = null,
        IEnumerable<string>? permissions = null,
        bool isDefault = false) =>
        new()
        {
            Id = id,
            Name = NormalizeName(name),
            DisplayName = NormalizeOptionalText(displayName, MaxDisplayNameLength, nameof(displayName)),
            Description = NormalizeOptionalText(description, MaxDescriptionLength, nameof(description)),
            IsBuiltIn = true,
            IsDefault = isDefault,
            Permissions = NormalizePermissions(permissions),
        };

    /// <summary>
    /// 覆盖式设置权限集。<b>内置角色拒绝修改</b>。
    /// </summary>
    /// <param name="permissions">新的权限集；不得含目录之外的权限。</param>
    /// <exception cref="InvalidOperationException">试图修改内置角色的权限集。</exception>
    /// <exception cref="ArgumentException">权限标识不在目录中，或为空串。</exception>
    /// <remarks>
    /// <b>为什么内置角色的权限集锁死</b>：一个能被清空权限集的 <c>operator</c>，
    /// 已经不是它的名字所声称的那个角色。更实际的风险是管理员把 <c>admin</c> 的权限改窄，
    /// 把自己锁在门外且只能靠改库恢复。让内置角色的语义随迁移固定，
    /// 自定义角色则留给管理员自由折腾。</remarks>
    public void SetPermissions(IEnumerable<string> permissions)
    {
        if (this.IsBuiltIn)
        {
            throw new InvalidOperationException($"内置角色 '{this.Name}' 的权限集不可修改");
        }

        this.Permissions = NormalizePermissions(permissions);
    }

    /// <summary>
    /// 判断本角色是否具备指定权限。
    /// </summary>
    /// <param name="permission">权限标识（大小写不敏感，可含首尾空白）。</param>
    /// <returns>具备返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 目录之外的标识一律返回 <see langword="false"/> 而<b>不</b>抛异常：
    /// 鉴权路径上的每一次「没有这条权限」判定都是正常的否，而不是错误。
    /// 真正需要校验输入的地方在写入侧（<see cref="SetPermissions"/>）。
    /// </remarks>
    public bool HasPermission(string? permission) =>
        !String.IsNullOrWhiteSpace(permission) &&
        this.Permissions.Contains(permission.Trim().ToLowerInvariant(), StringComparer.Ordinal);

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
    /// 「这个角色仍有有效分配」与「至少保留一名管理员」都是<b>跨行</b>规则，不在实体里判定，
    /// 由应用服务在调用本方法前检查。前者问分配表（<c>CountByRoleIdAsync</c>），
    /// 后者问全局分配（主机分配里的 admin 不构成系统管理员）。
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

    /// <summary>
    /// 规范化权限集：转小写、去重、<b>按目录顺序排序</b>。
    /// </summary>
    /// <param name="permissions">原始权限集合；可为 <see langword="null"/>（视作空集）。</param>
    /// <returns>规范化后的权限集（不可变快照）。</returns>
    /// <exception cref="ArgumentException">存在空串或目录之外的权限标识。</exception>
    /// <remarks>
    /// <b>为什么按目录顺序而不是字母序</b>：接口返回的权限数组与权限矩阵的列顺序都由目录决定，
    /// 数据库返回的顺序则不确定（<c>SELECT</c> 不承诺排序）。若不在此处归一，
    /// 同一个角色两次读出来的权限数组顺序可能不同，前端的勾选框就会跟着抖。
    /// 目录顺序（Host 在前、Global 在后）也是界面上的分组顺序。
    /// </remarks>
    private static IReadOnlyList<string> NormalizePermissions(IEnumerable<string>? permissions)
    {
        if (permissions is null)
        {
            return [];
        }

        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var permission in permissions)
        {
            if (String.IsNullOrWhiteSpace(permission))
            {
                throw new ArgumentException("权限标识不得为空", nameof(permissions));
            }

            if (!Permission.Exists(permission))
            {
                throw new ArgumentException($"未知的权限标识：{permission}", nameof(permissions));
            }

            set.Add(permission.Trim().ToLowerInvariant());
        }

        return [.. Permission.All.Where(set.Contains)];
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
