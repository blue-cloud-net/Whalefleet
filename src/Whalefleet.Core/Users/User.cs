namespace Whalefleet.Core.Users;

/// <summary>
/// 系统用户。
/// </summary>
/// <remarks>
/// <para><b>充血职责</b>：本实体负责用户名的格式不变量与密码哈希的「非空」约束。
/// 「系统里至少要保留一名管理员」「用户名是否已被占用」这类<b>需要查库 / 跨行</b>的规则不属于实体，
/// 由应用服务在调用实体方法前判定。</para>
/// <para><b>用户没有角色字段</b>（0.1.0 起）：角色与用户是<b>多对多且带作用域</b>的，
/// 由 <c>user_role_assignments</c> 表表达（见 <c>Core/Permissions/UserRoleAssignment</c>）。
/// 在此之前 <c>User.RoleId</c> 指向单条 <see cref="Role"/>，那个模型表达不了
/// 「在 A 主机是运维、在 B 主机只读」。</para>
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
    /// 是否已停用（0.13.0）。
    /// </summary>
    /// <remarks>
    /// <para><b>停用与删除是两件事</b>：删除是软删除（行仍在库，只是不再参与查询），
    /// 而停用保留账号与它的全部审计历史，只是拒绝登录。</para>
    /// <para>停用后该用户已签发的会话会全部被撤销；即便旧 access token 尚未过期，
    /// 每次请求的会话校验也会因 <c>is_disabled</c> 而拒绝。</para>
    /// </remarks>
    public bool IsDisabled { get; private set; }

    /// <summary>
    /// 是否必须先修改密码才能继续使用（0.13.0）。
    /// </summary>
    /// <remarks>
    /// 置位时机：① 新建用户（按安全设置 <c>must_change_on_first_login</c>）；
    /// ② 密码超过有效期。改密成功后清除。
    /// </remarks>
    public bool MustChangePassword { get; private set; }

    /// <summary>
    /// 最近一次密码变更时间（UTC）；<see langword="null"/> 表示从未变更。
    /// </summary>
    /// <remarks>
    /// 用于计算密码有效期。<b>不回填到历史行</b>——迁移里对存量行回填的是创建时间，
    /// 那已经是历史事实的近似；应用层新建账号时则显式写入当前时间。
    /// </remarks>
    public DateTime? PasswordChangedAt { get; private set; }

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
    /// <param name="passwordChangedAt">
    /// 密码变更时间（UTC）；省略表示<b>从未变更</b>。新建账号应当显式传入当前时间——
    /// 否则密码有效期无从计算。
    /// </param>
    /// <returns>新建的用户实体。</returns>
    /// <exception cref="ArgumentException">
    /// 用户名为空、长度越界、含非法字符，或密码哈希为空。
    /// </exception>
    /// <remarks>
    /// <b>创建用户不会附带任何角色</b>：角色是<b>分配</b>给用户的（<c>user_role_assignments</c>），
    /// 应用服务在创建后另外建立默认角色的全局分配。
    /// 把角色写进本实体会让它看起来像「用户天生就属于某个角色」，而那不是事实。
    /// </remarks>
    public static User Create(Guid id, string username, string passwordHash, DateTime? passwordChangedAt = null)
    {
        return new User
        {
            Id = id,
            Username = NormalizeUsername(username),
            PasswordHash = EnsurePasswordHash(passwordHash),
            PasswordChangedAt = passwordChangedAt,
        };
    }

    /// <summary>
    /// 更换密码哈希。
    /// </summary>
    /// <param name="passwordHash">新的密码哈希（不得为空）。</param>
    /// <param name="utcNow">变更时间（UTC），用于重置密码有效期与首次改密标记。</param>
    /// <exception cref="ArgumentException">密码哈希为空。</exception>
    /// <remarks>
    /// <b>改密必然清除「必须改密」标记</b>：否则用户改完密码仍被拦在门外，
    /// 且没有任何途径能解开——那是一个死锁。
    /// </remarks>
    public void ChangePassword(string passwordHash, DateTime utcNow)
    {
        this.PasswordHash = EnsurePasswordHash(passwordHash);
        this.PasswordChangedAt = utcNow;
        this.MustChangePassword = false;
    }

    /// <summary>
    /// 标记为「必须先改密」。
    /// </summary>
    /// <remarks>
    /// 幂等：重复标记无副作用。
    /// </remarks>
    public void MarkPasswordChangeRequired()
    {
        this.MustChangePassword = true;
    }

    /// <summary>
    /// 停用账号。
    /// </summary>
    /// <remarks>
    /// <b>停用不撤销会话</b>：那是应用服务的职责（它需要同时写审计并清缓存）。
    /// 实体只管自己的状态。
    /// </remarks>
    public void Disable()
    {
        this.IsDisabled = true;
    }

    /// <summary>
    /// 启用账号。
    /// </summary>
    /// <remarks>
    /// <b>启用不恢复已撤销的会话</b>：被吊销的会话不会因为账号恢复而复活。
    /// </remarks>
    public void Enable()
    {
        this.IsDisabled = false;
    }

    /// <summary>
    /// 规范化用户名。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <returns>规范化后的用户名。</returns>
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

    /// <summary>
    /// 判断字符是否允许出现在用户名中。
    /// </summary>
    /// <param name="character">待判断的字符。</param>
    /// <returns>允许时为 <see langword="true"/>。</returns>
    private static bool IsAllowedUsernameCharacter(char character) =>
        Char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    /// <summary>
    /// 校验口令哈希非空。
    /// </summary>
    /// <param name="passwordHash">口令哈希。</param>
    private static string EnsurePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return passwordHash;
    }
}
