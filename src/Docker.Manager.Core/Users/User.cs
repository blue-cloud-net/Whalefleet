namespace Docker.Manager.Core.Users;

/// <summary>
/// 系统用户。
/// </summary>
/// <remarks>
/// <para><b>充血职责</b>：本实体负责用户名的格式不变量与密码哈希的「非空」约束。
/// 「系统里至少要保留一名管理员」「用户名是否已被占用」「该角色是否存在」这类
/// <b>需要查库/跨行</b>的规则不属于实体，由应用服务在调用实体方法前判定。</para>
/// <para><b>角色是外键不是枚举</b>：<see cref="RoleId"/> 指向 <c>roles</c> 表的一条记录
/// （见 <see cref="Role"/>）——角色可在运行时增删，枚举做不到。
/// 这也意味着「用户拥有某个角色」<b>不是</b>静态类型保证：拿到 <see cref="RoleId"/> 之后
/// 仍需查库确认角色存在，因此引用完整性由数据库外键兜底。</para>
/// <para><b>用户名不可变</b>：它同时是登录凭据与唯一业务键，允许改名会让唯一约束与审计同时复杂化。
/// 需要「换名」时删除旧用户、新建用户。</para>
/// <para><b>删除语义</b>：软删除（继承 <see cref="FullAuditedEntity{TId, TUserKey}"/>），
/// 因此被删除的用户名可以再次使用——唯一索引带 <c>WHERE is_deleted = 0</c> 条件。</para>
/// <para><b>密码不落明文</b>：<see cref="PasswordHash"/> 只接受<b>已哈希</b>的结果；
/// 哈希与校验由应用层的密码哈希器负责（<c>Core</c> 不引用任何加密库）。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class User : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 用户名最小长度（去首尾空白后）。
    /// </summary>
    public const int MinUsernameLength = 3;

    /// <summary>
    /// 用户名最大长度（去首尾空白后）。
    /// </summary>
    public const int MaxUsernameLength = 32;

    /// <summary>
    /// 用户名（去首尾空白后保存，比较时<b>不区分大小写</b>）。
    /// </summary>
    public string Username { get; private set; } = String.Empty;

    /// <summary>
    /// 密码哈希（Argon2id 编码串），<b>绝不是明文密码</b>。
    /// </summary>
    public string PasswordHash { get; private set; } = String.Empty;

    /// <summary>
    /// 角色主键，指向 <c>roles</c> 表；不得为 <see cref="Guid.Empty"/>。
    /// </summary>
    public Guid RoleId { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private User()
    {
    }

    /// <summary>
    /// 创建新用户。
    /// </summary>
    /// <param name="id">用户主键（Guid.Empty 表示尚未分配，由调用方保证非空）。</param>
    /// <param name="username">用户名；首尾空白会被去除，长度 3-32，仅允许 ASCII 字母、数字与 <c>. _ -</c>。</param>
    /// <param name="passwordHash">密码哈希（不得为明文，也不得为空）。</param>
    /// <param name="roleId">角色主键；不得为 <see cref="Guid.Empty"/>（未指定角色时应传入默认角色的主键）。</param>
    /// <returns>新建的用户实体。</returns>
    /// <exception cref="ArgumentException">
    /// 用户名为空、长度越界、含非法字符，密码哈希为空，或 <paramref name="roleId"/> 为空。
    /// </exception>
    public static User Create(Guid id, string username, string passwordHash, Guid roleId)
    {
        EnsureValidRoleId(roleId);

        return new User
        {
            Id = id,
            Username = NormalizeUsername(username),
            PasswordHash = EnsurePasswordHash(passwordHash),
            RoleId = roleId,
        };
    }

    /// <summary>
    /// 更换密码哈希。
    /// </summary>
    /// <param name="passwordHash">新的密码哈希（不得为空）。</param>
    /// <exception cref="ArgumentException">密码哈希为空。</exception>
    public void ChangePassword(string passwordHash)
    {
        this.PasswordHash = EnsurePasswordHash(passwordHash);
    }

    /// <summary>
    /// 变更角色。
    /// </summary>
    /// <param name="roleId">新角色主键；不得为 <see cref="Guid.Empty"/>。</param>
    /// <exception cref="ArgumentException"><paramref name="roleId"/> 为空。</exception>
    /// <remarks>「不得移除最后一名管理员」的校验在应用服务层，本方法不做。</remarks>
    public void ChangeRole(Guid roleId)
    {
        EnsureValidRoleId(roleId);
        this.RoleId = roleId;
    }

    private static string NormalizeUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var trimmed = username.Trim();
        if (trimmed.Length is < MinUsernameLength or > MaxUsernameLength)
        {
            throw new ArgumentException(
                $"用户名长度必须在 {MinUsernameLength}-{MaxUsernameLength} 个字符之间",
                nameof(username));
        }

        foreach (var character in trimmed)
        {
            if (!IsAllowedUsernameCharacter(character))
            {
                throw new ArgumentException(
                    $"用户名含非法字符 '{character}'：只允许 ASCII 字母、数字与 . _ -",
                    nameof(username));
            }
        }

        return trimmed;
    }

    private static bool IsAllowedUsernameCharacter(char character) =>
        Char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static string EnsurePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return passwordHash;
    }

    private static void EnsureValidRoleId(Guid roleId)
    {
        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("用户必须归属一个角色", nameof(roleId));
        }
    }
}
