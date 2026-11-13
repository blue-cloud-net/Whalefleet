using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Application.Security;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Auth;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;

namespace Whalefleet.Application.Users;

/// <summary>
/// 用户管理用例（对应 <c>/api/v1/admin/users/*</c>）。
/// </summary>
/// <remarks>
/// <para><b>「管理员」判定在服务层也要做一遍</b>：接口层的 <c>[Authorize(Roles)]</c> 挡住的是
/// 「能不能进这个端点」，而「改自己密码」与「改他人密码」的差别是**数据相关**的
/// （取决于请求体里的 <c>userId</c>），只能在服务层判。两者不是重复，而是不同层次的问题。</para>
/// <para><b>密码变更一律撤销该用户全部刷新令牌</b>：改密码的常见动因就是「疑似泄漏」，
/// 不动会话等于把旧凭据留在原地。管理员重置他人密码同理。</para>
/// <para><b>日志纪律</b>：只记用户名与操作者，<b>绝不</b>记密码（连"密码前几位"也不行）。</para>
/// </remarks>
public sealed class UserService
{
    /// <summary>
    /// 列表默认每页条数。
    /// </summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// 列表每页条数上限。
    /// </summary>
    /// <remarks>夹紧而不是报错：分页参数是展示偏好，为此打断一次查询不值得；上限防止一次拉全表。</remarks>
    public const int MaxPageSize = 100;

    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IUserRoleAssignmentRepository _assignments;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISecuritySettingsStore _securitySettings;
    private readonly ISessionStatusCache _sessionCache;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditLogger _audit;
    private readonly ILogger<UserService> _logger;

    /// <summary>
    /// 初始化用户管理用例。
    /// </summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="roles">角色仓储。</param>
    /// <param name="assignments">分配仓储（角色的多对多关系；创建用户与删除管理员都走它）。</param>
    /// <param name="refreshTokens">刷新令牌仓储（0.13.0 起它同时是会话表）。</param>
    /// <param name="passwordHasher">密码哈希器。</param>
    /// <param name="securitySettings">安全策略存储（密码强度与有效期）。</param>
    /// <param name="sessionCache">会话状态缓存（吊销 / 停用后即时失效）。</param>
    /// <param name="currentUser">当前登录用户（判断"改自己"还是"改别人"）。</param>
    /// <param name="timeProvider">时间源（便于测试）。</param>
    /// <param name="audit">审计日志。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public UserService(
        IUserRepository users,
        IRoleRepository roles,
        IUserRoleAssignmentRepository assignments,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        ISecuritySettingsStore securitySettings,
        ISessionStatusCache sessionCache,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        IAuditLogger audit,
        ILogger<UserService> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(securitySettings);
        ArgumentNullException.ThrowIfNull(sessionCache);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _users = users;
        _roles = roles;
        _assignments = assignments;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _securitySettings = securitySettings;
        _sessionCache = sessionCache;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// 分页列出用户。
    /// </summary>
    /// <param name="request">分页参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分页结果（含总条数）。</returns>
    public async Task<UserListResult> ListAsync(
        ListUsersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pageIndex = Math.Max(0, request.PageIndex ?? 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        var total = await _users.CountAsync(cancellationToken).ConfigureAwait(false);

        var users = await _users
            .ListAsync(pageIndex * pageSize, pageSize, cancellationToken)
            .ConfigureAwait(false);

        // 角色表与分配各取一次后在内存里映射：逐行查库是 N+1，
        // 而角色总共只有十几条，一个用户的分配也只有个位数。
        var nameByRoleId = (await _roles.QueryAllListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(role => role.Id, role => role.Name);

        var userIds = users.Select(user => user.Id).ToArray();
        var assignments = await _assignments.ListByUsersAsync(userIds, cancellationToken).ConfigureAwait(false);

        var rolesByUser = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var group in assignments.GroupBy(assignment => assignment.UserId))
        {
            rolesByUser[group.Key] = MapRoleNames(group, nameByRoleId);
        }

        var items = users
            .Select(user => new UserSummary(
                user.Id,
                user.Username,
                rolesByUser.TryGetValue(user.Id, out var names) ? names : [],
                user.CreationTime,
                user.IsDisabled,
                user.MustChangePassword))
            .ToList();

        return new UserListResult(items, total, pageIndex, pageSize);
    }

    /// <summary>
    /// 创建用户。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新用户。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）、用户名已存在（<c>1200</c>）或密码强度不足（<c>1202</c>）。
    /// </exception>
    public async Task<UserSummary> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var username = Require(request.Username, "username");
        var password = Require(request.Password, "password");

        if (await _users.ExistsByUsernameAsync(username, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.UsernameAlreadyExists,
                $"用户名「{username}」已被使用（不区分大小写）");
        }

        // 与初始管理员引导共用同一套强度规则：两处用不同规则迟早出现"能建出来的账号登不进"。
        // 0.13.0 起规则参数可配，因此这里读的是实例设置。
        var policy = await _securitySettings.GetAsync(cancellationToken).ConfigureAwait(false);
        PasswordPolicy.EnsureSatisfied(password, policy);

        var role = await this.ResolveRoleAsync(request.Role, cancellationToken).ConfigureAwait(false);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var user = User.Create(Guid.NewGuid(), username, _passwordHasher.Hash(password), utcNow);

        // 「首次登录强制改密」：新建的账号默认置位，用户改完密码后清除。
        if (policy.MustChangeOnFirstLogin)
        {
            user.MarkPasswordChangeRequired();
        }

        await _users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        // 角色是「分配」给用户的，不是用户自带的：创建用户后另外建一条全局分配。
        // 写成 User.RoleId 会让「默认角色」变成用户实体的一部分，
        // 而那件事在多角色模型里根本不属于用户。
        await _assignments
            .AddAsync(UserRoleAssignment.Assign(Guid.NewGuid(), user.Id, role.Id), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("已创建用户 {Username}（角色={Role}）", user.Username, role.Name);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.UserCreate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.User,
                TargetId = user.Id.ToString("D"),
                TargetName = user.Username,
                Detail = $"初始角色={role.Name}",
            },
            cancellationToken).ConfigureAwait(false);

        return new UserSummary(
            user.Id,
            user.Username,
            [role.Name],
            user.CreationTime,
            user.IsDisabled,
            user.MustChangePassword);
    }

    /// <summary>
    /// 删除用户（软删除）并撤销其全部会话。
    /// </summary>
    /// <param name="request">删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 未提供 <c>userId</c>（<c>1000</c>）、目标不存在（<c>1201</c>），
    /// 或属于被保护的账号（<c>1000</c>：当前登录账号 / 最后一个管理员）。
    /// </exception>
    public async Task DeleteAsync(
        DeleteUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId is not { } userId)
        {
            throw BusinessException.Validation("userId 不能为空");
        }

        var target = await _users.QueryByIdAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.UserNotFound, "用户不存在");

        if (target.Id == _currentUser.UserId)
        {
            throw BusinessException.Validation("不能删除当前登录的账号");
        }

        await this.EnsureNotLastAdminAsync(target, cancellationToken).ConfigureAwait(false);

        await _users.RemoveByIdAsync(target.Id, cancellationToken).ConfigureAwait(false);

        // 删号后旧凭据不该继续有效：否则被删的人还能拿手里的 refresh token 换新 access token。
        await this.RevokeUserSessionsAsync(target.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("已删除用户 {Username}", target.Username);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.UserRemove,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.User,
                TargetId = target.Id.ToString("D"),

                // 用户被删后名字也查不到了，这里必须存快照。
                TargetName = target.Username,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 修改密码（自助改密需验原密码；管理员重置他人不需要）。
    /// </summary>
    /// <param name="request">改密请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法或原密码不正确（<c>1000</c>）、目标不存在（<c>1201</c>）、
    /// 新密码强度不足（<c>1202</c>）、无权重置他人（<c>1500</c>）。
    /// </exception>
    public async Task UpdatePasswordAsync(
        UpdatePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isSelf = request.UserId is null || request.UserId == _currentUser.UserId;
        var target = await this.ResolvePasswordTargetAsync(request, isSelf, cancellationToken)
            .ConfigureAwait(false);

        if (!isSelf && !this.IsAdministrator)
        {
            throw new BusinessException(
                ErrorCodes.ResourceForbidden,
                "只能修改自己的密码；重置他人密码需要管理员权限");
        }

        if (isSelf)
        {
            this.VerifyOldPassword(target, Require(request.OldPassword, "oldPassword"));
        }

        var newPassword = Require(request.NewPassword, "newPassword");

        var policy = await _securitySettings.GetAsync(cancellationToken).ConfigureAwait(false);
        PasswordPolicy.EnsureSatisfied(newPassword, policy);

        // 改密会清掉「必须改密」标记（实体方法内完成）并更新密码变更时间。
        target.ChangePassword(_passwordHasher.Hash(newPassword), _timeProvider.GetUtcNow().UtcDateTime);

        await _users.UpdateAsync(target, cancellationToken).ConfigureAwait(false);

        // 密码变更的常见动因就是"疑似泄漏"：不动会话等于把旧凭据留在原地。
        await this.RevokeUserSessionsAsync(target.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "已修改用户 {Username} 的密码（操作者={ActorId}，自助={IsSelf}）",
            target.Username,
            _currentUser.UserId,
            isSelf);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.UserUpdatePassword,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.User,
                TargetId = target.Id.ToString("D"),
                TargetName = target.Username,
                Detail = $"自助={isSelf}（密码内容不入库）",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 列出某用户的<b>活跃</b>会话（0.13.0）。
    /// </summary>
    /// <param name="request">请求（目标用户）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话列表；无活跃会话时返回空列表。</returns>
    /// <exception cref="BusinessException">
    /// 未提供 <c>userId</c>（<c>1000</c>）或用户不存在（<c>1201</c>）。
    /// </exception>
    public async Task<IReadOnlyList<UserSessionSummary>> ListSessionsAsync(
        ListUserSessionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequireUserId(request.UserId);
        var user = await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await _refreshTokens
            .ListActiveByUserAsync(userId, utcNow, cancellationToken)
            .ConfigureAwait(false);

        return [.. sessions.Select(session => new UserSessionSummary(
            session.Id,
            user.Username,
            session.CreationTime,
            session.ExpiresAt,
            session.RotatedAt,
            session.CreatedIp,
            session.UserAgent,
            session.DeviceFingerprint))];
    }

    /// <summary>
    /// 吊销单个会话（0.13.0）。
    /// </summary>
    /// <param name="request">请求（目标用户 + 会话 id）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、用户不存在（<c>1201</c>）或会话不属于该用户（<c>1100</c>）。
    /// </exception>
    public async Task RevokeSessionAsync(
        RevokeUserSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequireUserId(request.UserId);
        var user = await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);

        if (request.SessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            throw BusinessException.Validation("sessionId 不能为空");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var session = await _refreshTokens
            .QueryByIdAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        // 会话不存在 → 幂等成功（它已经没了，吊销它没有意义）。
        // 会话存在但属于别人 → 明确拒绝：否则能用别人的会话 id 探测它的存在性。
        if (session is null)
        {
            return;
        }

        if (session.UserId != userId)
        {
            throw new BusinessException(ErrorCodes.Unauthenticated, "该会话不属于此用户");
        }

        await _refreshTokens.RevokeByIdAsync(sessionId, utcNow, cancellationToken).ConfigureAwait(false);
        _sessionCache.Remove(sessionId);

        _logger.LogInformation(
            "已吊销用户 {Username} 的会话 {SessionId}（操作者={ActorId}）",
            user.Username,
            sessionId,
            _currentUser.UserId);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.UserSessionRevoke,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.User,
                TargetId = user.Id.ToString("D"),
                TargetName = user.Username,
                Detail = $"会话={sessionId:D}",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 吊销某用户的<b>全部</b>会话（0.13.0）。
    /// </summary>
    /// <param name="request">请求（目标用户）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被吊销的会话数。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）或用户不存在（<c>1201</c>）。
    /// </exception>
    public async Task<int> RevokeAllSessionsAsync(
        ListUserSessionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequireUserId(request.UserId);
        var user = await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var revoked = await _refreshTokens
            .RevokeAllForUserAsync(userId, utcNow, cancellationToken)
            .ConfigureAwait(false);

        // 缓存也要清：否则最多 60 秒内那个会话仍会被当作有效。
        _sessionCache.RemoveByUser(userId);

        _logger.LogInformation(
            "已吊销用户 {Username} 的全部会话（共 {Count} 条，操作者={ActorId}）",
            user.Username,
            revoked,
            _currentUser.UserId);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.UserSessionRevokeAll,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.User,
                TargetId = user.Id.ToString("D"),
                TargetName = user.Username,
                Detail = $"共吊销 {revoked} 条会话",
            },
            cancellationToken).ConfigureAwait(false);

        return revoked;
    }

    /// <summary>
    /// 启用 / 停用账号（0.13.0）。
    /// </summary>
    /// <param name="request">请求（目标用户 + 是否启用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、用户不存在（<c>1201</c>）、
    /// 目标是当前登录账号或最后一个管理员（<c>1000</c>）。
    /// </exception>
    public async Task SetEnabledAsync(
        SetUserEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = RequireUserId(request.UserId);
        var target = await this.RequireUserAsync(userId, cancellationToken).ConfigureAwait(false);

        // enabled 省略时校验失败：默认为「不启用」会变成一个误调用就锁号的接口。
        if (request.Enabled is not { } enabled)
        {
            throw BusinessException.Validation("enabled 不能为空");
        }

        if (!enabled)
        {
            if (target.Id == _currentUser.UserId)
            {
                throw BusinessException.Validation("不能停用当前登录的账号");
            }

            // 最后一个管理员被停用后无人能管理系统 —— 与「不能删最后一个管理员」同一条不变式。
            await this.EnsureNotLastAdminAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (target.IsDisabled == !enabled)
        {
            // 状态没变：幂等返回，不重复写审计。
            return;
        }

        if (enabled)
        {
            target.Enable();
        }
        else
        {
            target.Disable();
        }

        await _users.UpdateAsync(target, cancellationToken).ConfigureAwait(false);

        if (!enabled)
        {
            // 停用必须**立即**让已签发的会话失效：
            // ① 撤销全部会话（库即事实源）；② 清缓存（否则最多 60 秒内仍被当作有效）。
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            await _refreshTokens
                .RevokeAllForUserAsync(target.Id, utcNow, cancellationToken)
                .ConfigureAwait(false);

            _sessionCache.RemoveByUser(target.Id);
        }

        _logger.LogInformation(
            "已{Action}用户 {Username}（操作者={ActorId}）",
            enabled ? "启用" : "停用",
            target.Username,
            _currentUser.UserId);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = enabled ? AuditActions.UserEnable : AuditActions.UserDisable,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.User,
                TargetId = target.Id.ToString("D"),
                TargetName = target.Username,
                Detail = enabled ? "启用账号" : "停用账号并撤销其全部会话",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 查询某用户是否必须先改密（0.13.0）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>需要强制改密返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>两个来源都要看</b>：用户身上的「必须改密」标记，以及密码超过有效期。
    /// 后者由安全设置驱动——改了有效期后，历史账号也会在下次登录时被要求改密。
    /// </remarks>
    public async Task<bool> GetMustChangePasswordAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.QueryByIdAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return false;
        }

        if (user.MustChangePassword)
        {
            return true;
        }

        var policy = await _securitySettings.GetAsync(cancellationToken).ConfigureAwait(false);

        return policy.IsPasswordExpired(user.PasswordChangedAt, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// 把一个用户的分配翻译成角色名数组。
    /// </summary>
    /// <param name="assignments">该用户的全部分配（全局 + 各主机）。</param>
    /// <param name="nameByRoleId">角色主键 → 角色名。</param>
    /// <returns>去重并按字典序排列的角色名数组。</returns>
    /// <remarks>
    /// <b>刻意<b>不</b>按「全局分配优先」排序</b>：同一用户既可能是管理员又是运维，
    /// 而列表只是给人看的。排序规则一旦有业务含义，界面就会开始依赖它。
    /// 查不到的角色（已软删除）被静默跳过而不是留空串——空串会让界面渲染出一个空标签。
    /// </remarks>
    private static IReadOnlyList<string> MapRoleNames(
        IEnumerable<UserRoleAssignment> assignments,
        Dictionary<Guid, string> nameByRoleId) =>
        [
            .. assignments
                .Select(assignment => assignment.RoleId)
                .Where(nameByRoleId.ContainsKey)
                .Select(roleId => nameByRoleId[roleId])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

    /// <summary>
    /// 读取必填字段，缺失或空白时抛参数异常。
    /// </summary>
    /// <param name="value">字段值。</param>
    /// <param name="field">字段名（用于异常信息）。</param>
    /// <returns>去除首尾空白后的字段值。</returns>
    private static string Require(string? value, string field)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        return value.Trim();
    }

    private bool IsAdministrator =>
        _currentUser.Roles.Contains(Role.AdminName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解析改密请求的目标用户。
    /// </summary>
    /// <param name="request">改密请求。</param>
    /// <param name="isSelf">是否为改自己的密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>目标用户。</returns>
    private async Task<User> ResolvePasswordTargetAsync(
        UpdatePasswordRequest request,
        bool isSelf,
        CancellationToken cancellationToken)
    {
        // 改自己：用当前登录用户；改他人：按 userId 查。
        var userId = isSelf ? _currentUser.UserId : request.UserId;

        if (userId is not { } id)
        {
            throw new BusinessException(ErrorCodes.Unauthenticated, "无法确定当前用户");
        }

        return await _users.QueryByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.UserNotFound, "用户不存在");
    }

    /// <summary>
    /// 校验旧密码是否匹配。
    /// </summary>
    /// <param name="target">目标用户。</param>
    /// <param name="oldPassword">请求携带的旧密码明文。</param>
    private void VerifyOldPassword(User target, string oldPassword)
    {
        // Verify 不抛异常（见 IPasswordHasher），因此这里只需判返回值。
        if (!_passwordHasher.Verify(oldPassword, target.PasswordHash))
        {
            throw BusinessException.Validation("原密码不正确");
        }
    }

    /// <summary>
    /// 保证不会把最后一个管理员删掉。
    /// </summary>
    /// <param name="target">待删除的用户。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">目标是最后一个管理员（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>触达面</b>：走 HTTP 时，能删人的必定是管理员，而「只剩一个管理员」意味着那个管理员就是
    /// 调用者本人，因此会先被「不能删除当前登录的账号」拦住——这道检查在接口层实际上撞不到。
    /// 保留它是因为「系统里至少要有一个管理员」是**不变式**，不该只在 HTTP 这一条路径上成立：
    /// 后台维护、迁移脚本等无请求上下文的调用者同样不能把它删空。
    /// </remarks>
    private async Task EnsureNotLastAdminAsync(User target, CancellationToken cancellationToken)
    {
        var adminRole = await _roles.FindByNameAsync(Role.AdminName, cancellationToken).ConfigureAwait(false);

        if (adminRole is null)
        {
            return;
        }

        var targetCount = await _assignments
            .CountGlobalByUserAndRoleAsync(target.Id, adminRole.Id, cancellationToken)
            .ConfigureAwait(false);

        if (targetCount == 0)
        {
            // 目标不是管理员（主机分配里的 admin 不算系统管理员），本规则不适用
            return;
        }

        var adminCount = await _assignments.CountGlobalByRoleAsync(adminRole.Id, cancellationToken)
            .ConfigureAwait(false);

        if (adminCount <= 1)
        {
            throw BusinessException.Validation("这是最后一个管理员账号，删除后将无人能管理系统");
        }
    }

    /// <summary>
    /// 按角色名解析角色；未指定时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="roleName">角色名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色；未指定时为 <see langword="null"/>。</returns>
    private async Task<Role> ResolveRoleAsync(string? roleName, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(roleName))
        {
            // 省略角色 → 默认角色。默认角色至多一个由数据库部分唯一索引保证，这里只做兜底提示。
            return await _roles.GetDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw BusinessException.Validation("系统没有默认角色，请在创建时显式指定 role");
        }

        var name = roleName.Trim();

        return await _roles.FindByNameAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw BusinessException.Validation($"角色「{name}」不存在");
    }

    /// <summary>
    /// 撤销该用户的全部会话。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RevokeUserSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var revoked = await _refreshTokens
            .RevokeAllForUserAsync(userId, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);

        // 缓存同步清理：否则最多 60 秒内那些会话仍会被当作有效。
        _sessionCache.RemoveByUser(userId);

        if (revoked > 0)
        {
            _logger.LogInformation("已撤销用户 {UserId} 的 {Count} 个会话", userId, revoked);
        }
    }

    /// <summary>
    /// 读取必填的用户主键。
    /// </summary>
    /// <param name="userId">用户主键（可空）。</param>
    /// <returns>用户主键。</returns>
    /// <exception cref="BusinessException">未提供（<c>1000</c>）。</exception>
    private static Guid RequireUserId(Guid? userId)
    {
        if (userId is not { } id || id == Guid.Empty)
        {
            throw BusinessException.Validation("userId 不能为空");
        }

        return id;
    }

    /// <summary>
    /// 确认目标用户存在。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户实体。</returns>
    /// <exception cref="BusinessException">用户不存在（<c>1201</c>）。</exception>
    private async Task<User> RequireUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _users.QueryByIdAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.UserNotFound, "用户不存在");
    }
}
