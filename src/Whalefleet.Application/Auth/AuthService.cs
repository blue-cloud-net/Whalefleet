using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Auth;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Auth;

/// <summary>
/// 认证用例服务：用户名 / 密码登录。
/// </summary>
/// <remarks>
/// <para><b>依赖为什么这么多</b>：登录天然要串联"查用户 → 校验密码 → 查角色 → 签发令牌 → 记录刷新令牌"
/// 五个步骤，这正是一个用例编排服务该做的事。它<b>不</b>实现任何算法或持久化细节。</para>
/// <para><b>生命周期为 Scoped</b>：它依赖 Scoped 的仓储（其取值来自当前请求的数据库连接工厂）。</para>
/// <para><b>两条安全准则</b>（均有专门的测试守护）：</para>
/// <list type="number">
///   <item><description><b>响应不可区分</b>：用户名不存在与密码错误返回完全相同的错误码与文案。</description></item>
///   <item><description><b>耗时不可区分</b>：用户名不存在时也执行一次等价成本的哑元校验，
///   否则响应时间本身就是枚举信号。</description></item>
/// </list>
/// <para><b>日志纪律</b>：只记用户名与客户端 IP；<b>绝不</b>记密码、令牌或哈希。</para>
/// </remarks>
public sealed class AuthService
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IUserRoleAssignmentRepository _assignments;
    private readonly IPermissionChecker _permissions;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ILoginAttemptTracker _attemptTracker;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditLogger _audit;
    private readonly ILogger<AuthService> _logger;

    /// <summary>
    /// 初始化 <see cref="AuthService"/>。
    /// </summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="roles">角色仓储（把分配里的角色主键翻译成角色名）。</param>
    /// <param name="assignments">分配仓储（用户的角色是多对多且带作用域的）。</param>
    /// <param name="permissions">权限判定（登录响应里回传全局有效权限集，供界面显隐菜单）。</param>
    /// <param name="refreshTokens">刷新令牌仓储。</param>
    /// <param name="passwordHasher">密码哈希器。</param>
    /// <param name="tokenService">令牌服务。</param>
    /// <param name="attemptTracker">登录失败计数器。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="audit">审计日志（认证日志的数据源）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AuthService(
        IUserRepository users,
        IRoleRepository roles,
        IUserRoleAssignmentRepository assignments,
        IPermissionChecker permissions,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ILoginAttemptTracker attemptTracker,
        TimeProvider timeProvider,
        IAuditLogger audit,
        ILogger<AuthService> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(attemptTracker);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _users = users;
        _roles = roles;
        _assignments = assignments;
        _permissions = permissions;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _attemptTracker = attemptTracker;
        _timeProvider = timeProvider;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// 凭据错误的统一响应消息。
    /// </summary>
    /// <remarks>
    /// 提为常量是为了让它只存在一处——只要"用户名不存在"与"密码错误"共用它，
    /// 就不可能出现两处文案漂移从而被用来枚举用户名。
    /// </remarks>
    private const string InvalidCredentialsMessage = "用户名或密码错误";

    /// <summary>
    /// 执行登录。
    /// </summary>
    /// <param name="request">登录请求。</param>
    /// <param name="clientIp">客户端 IP（用于失败计数与日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌与用户摘要。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="BusinessException">
    /// 用户名或密码为空（<c>1000</c>）；失败次数超限（<c>1103</c>）；
    /// 凭据错误（<c>1100</c>）；用户所属角色缺失（<c>9999</c>，数据被破坏）。
    /// </exception>
    public async Task<LoginResponse> LoginAsync(
        LoginRequest request,
        string clientIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 用户名归一化（去首尾空白）与大小写无关的匹配由仓储的 COLLATE NOCASE 承担；
        // 但失败计数的 key 必须归一化，否则换个大小写就能绕过限流。
        var username = request.Username?.Trim() ?? String.Empty;
        var password = request.Password ?? String.Empty;

        if (username.Length == 0 || password.Length == 0)
        {
            throw BusinessException.Validation("用户名与密码均不能为空");
        }

        // 先判限流：密码校验很贵，没必要为已被限制的请求付这个成本。
        if (_attemptTracker.IsBlocked(clientIp, username))
        {
            _logger.LogWarning("登录被限流（IP={ClientIp}，用户名={Username}）", clientIp, username);

            await this.RecordAuthenticationAsync(
                AuditActions.Login,
                AuditOutcome.Denied,
                username,
                actorId: null,
                ErrorCodes.LoginRateLimited,
                clientIp,
                cancellationToken).ConfigureAwait(false);

            throw new BusinessException(ErrorCodes.LoginRateLimited, "登录尝试过于频繁，请稍后再试");
        }

        var user = await _users.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            // 时序对齐：没有这个用户，也要付出一次等价的哈希计算成本。
            _passwordHasher.PerformDummyVerification(password);

            throw await this.FailLoginAsync(clientIp, username, cancellationToken).ConfigureAwait(false);
        }

        if (!_passwordHasher.Verify(password, user.PasswordHash))
        {
            throw await this.FailLoginAsync(clientIp, username, cancellationToken).ConfigureAwait(false);
        }

        var roleNames = await this.ResolveRoleNamesAsync(user.Id, cancellationToken).ConfigureAwait(false);

        _attemptTracker.Reset(clientIp, username);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var accessToken = _tokenService.CreateAccessToken(user.Id, user.Username, roleNames, utcNow);
        var refreshToken = _tokenService.CreateRefreshToken(utcNow);

        await _refreshTokens.AddAsync(
            RefreshToken.Issue(Guid.NewGuid(), user.Id, refreshToken.Hash, refreshToken.ExpiresAt),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("用户 {Username} 登录成功（IP={ClientIp}）", user.Username, clientIp);

        await this.RecordAuthenticationAsync(
            AuditActions.Login,
            AuditOutcome.Success,
            user.Username,
            user.Id,
            errorCode: null,
            clientIp,
            cancellationToken).ConfigureAwait(false);

        var globalPermissions = await _permissions
            .GetEffectivePermissionsAsync(user.Id, null, cancellationToken)
            .ConfigureAwait(false);

        return new LoginResponse(
            accessToken.Value,
            refreshToken.Raw,
            (int)Math.Max(0, (accessToken.ExpiresAt - utcNow).TotalSeconds),
            new AuthenticatedUser(
                user.Id,
                user.Username,
                roleNames,
                [.. Permission.All.Where(globalPermissions.Contains)]));
    }

    /// <summary>
    /// 解析用户的角色名集合（登录与刷新时给令牌与界面用）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>角色名数组（去重、大写无关比较后保留首次出现的大小写形式）。</returns>
    /// <remarks>
    /// <para><b>取全部分配而不是「默认那一个」</b>：多角色后只报一个名字会让界面只显示一个角色，
    /// 而用户实际拥有几个是必须如实呈现的事实。</para>
    /// <para><b>返回空数组是合法状态</b>：新建用户到被分配角色之间存在一个窗口期，
    /// 强行取「默认角色」会让他看起来像拥有自己还没有的权限。</para>
    /// <para><b>⚠️ 这不是授权事实</b>：它只供展示与菜单显隐。真正的判定走
    /// <see cref="IPermissionChecker"/>，因为带作用域的分配根本装不进令牌。</para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ResolveRoleNamesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var assignments = await _assignments.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        if (assignments.Count == 0)
        {
            return [];
        }

        var roles = await _roles
            .QueryListByIdsAsync([.. assignments.Select(assignment => assignment.RoleId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        return [.. roles.Select(role => role.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 用刷新令牌换取新的 access + refresh（<b>轮换</b>）。
    /// </summary>
    /// <param name="refreshToken">客户端持有的刷新令牌原文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的令牌对与用户摘要。</returns>
    /// <exception cref="BusinessException">
    /// 未提供令牌（<c>1000</c>）；令牌未知 / 已撤销 / 已过期 / 用户已不可用（<c>1102</c>）；
    /// 用户所属角色缺失（<c>9999</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>轮换</b>：刷新成功即撤销旧令牌，并通过 <c>ReplacedByTokenHash</c> 指向新令牌。
    /// 于是每个刷新令牌只能被使用一次，泄漏窗口被压缩到一个刷新周期以内。</para>
    /// <para><b>重放检测</b>：若「已被<b>轮换</b>的旧令牌」再次出现，说明它很可能已被窃取
    /// （合法客户端换到新令牌后不会再持有旧的），此时撤销该用户的<b>全部</b>刷新令牌、
    /// 强制重新登录。注意与「已登出的令牌被误用」区分——后者不牵连其他会话，
    /// 判据是 <c>ReplacedByTokenHash</c> 是否为空。</para>
    /// </remarks>
    public async Task<LoginResponse> RefreshAsync(
        string? refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(refreshToken))
        {
            throw BusinessException.Validation("缺少刷新令牌");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var stored = await _refreshTokens
            .FindByHashAsync(_tokenService.HashRefreshToken(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            // 未知令牌：可能是伪造、也可能是已被清理的过期记录。不作重放判定。
            _logger.LogWarning("刷新失败：令牌不存在");

            throw InvalidRefreshToken();
        }

        if (!stored.IsActiveAt(utcNow))
        {
            if (stored.ReplacedByTokenHash is not null)
            {
                // 已被轮换却再次出现 → 判定为重放。
                var revoked = await _refreshTokens
                    .RevokeAllForUserAsync(stored.UserId, utcNow, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogError(
                    "检测到刷新令牌重放，已撤销该用户的全部刷新令牌（用户={UserId}，共撤销 {Count} 条）",
                    stored.UserId,
                    revoked);

                // 重放是安全事件：必须进认证日志（用户已删除 / 令牌不存在等情况不记，避免噪声）。
                await this.RecordAuthenticationAsync(
                    AuditActions.RefreshTokenReplayed,
                    AuditOutcome.Failure,
                    stored.UserId.ToString("D"),
                    stored.UserId,
                    ErrorCodes.RefreshTokenRevoked,
                    clientIp: null,
                    cancellationToken,
                    detail: $"已撤销 {revoked} 条刷新令牌").ConfigureAwait(false);
            }
            else
            {
                // 已被登出（或由管理员撤销）后误用：只拒绝，不牵连其他会话。
                _logger.LogWarning("刷新失败：令牌已撤销（用户={UserId}）", stored.UserId);
            }

            throw InvalidRefreshToken();
        }

        var user = await _users.QueryByIdAsync(stored.UserId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            // 用户已被删除（软删除）而令牌仍在：拒绝，不签发新令牌。
            _logger.LogWarning("刷新失败：令牌所属用户已不存在（用户={UserId}）", stored.UserId);

            throw InvalidRefreshToken();
        }

        var roleNames = await this.ResolveRoleNamesAsync(user.Id, cancellationToken).ConfigureAwait(false);

        var accessToken = _tokenService.CreateAccessToken(user.Id, user.Username, roleNames, utcNow);
        var newRefreshToken = _tokenService.CreateRefreshToken(utcNow);

        // 先撤销旧的（并记录取代关系），再写入新的——两条写操作都在同一请求内，
        // 中途失败会留下"旧已撤销、新未写入"的状态，用户重新登录即可恢复，不会产生安全缺口。
        stored.Revoke(utcNow, newRefreshToken.Hash);
        await _refreshTokens.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

        await _refreshTokens
            .AddAsync(
                RefreshToken.Issue(Guid.NewGuid(), user.Id, newRefreshToken.Hash, newRefreshToken.ExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("用户 {Username} 刷新令牌成功（旧令牌已轮换）", user.Username);

        return new LoginResponse(
            accessToken.Value,
            newRefreshToken.Raw,
            (int)Math.Max(0, (accessToken.ExpiresAt - utcNow).TotalSeconds),
            new AuthenticatedUser(user.Id, user.Username, roleNames, []));
    }

    /// <summary>
    /// 登出：撤销当前刷新令牌。
    /// </summary>
    /// <param name="refreshToken">客户端持有的刷新令牌原文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">未提供令牌（<c>1000</c>）。</exception>
    /// <remarks>
    /// <para><b>幂等</b>：令牌不存在或已被撤销都视为成功。登出是清理动作，
    /// 因"令牌已经没用了"而报错只会让客户端在重试逻辑上绕圈子。</para>
    /// <para><b>不影响 access token</b>：它在到期前仍然有效（无状态 JWT 的固有性质）。
    /// 前端登出时必须同时清除本地的 access token——服务端无法让一个已签发的 JWT 立即失效
    /// （「让单个已签发的 JWT 立即失效」是一个尚未排期的议题）。</para>
    /// </remarks>
    public async Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(refreshToken))
        {
            throw BusinessException.Validation("缺少刷新令牌");
        }

        var stored = await _refreshTokens
            .FindByHashAsync(_tokenService.HashRefreshToken(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || stored.RevokedAt is not null)
        {
            // 幂等：已不存在或已撤销，直接视为登出成功。
            return;
        }

        stored.Revoke(_timeProvider.GetUtcNow().UtcDateTime);
        await _refreshTokens.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("用户已登出（用户={UserId}）", stored.UserId);

        await this.RecordAuthenticationAsync(
            AuditActions.Logout,
            AuditOutcome.Success,
            stored.UserId.ToString("D"),
            stored.UserId,
            errorCode: null,
            clientIp: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造统一的「刷新令牌无效」异常。
    /// </summary>
    /// <returns>错误码 <c>1102</c> 的业务异常。</returns>
    /// <remarks>
    /// 未知 / 已撤销 / 已过期 / 用户已删除共用同一文案：这些差异对攻击者有价值
    /// （可据此判断令牌是否曾经存在），对合法客户端则毫无用处。
    /// </remarks>
    private static BusinessException InvalidRefreshToken() =>
        new(ErrorCodes.RefreshTokenRevoked, "刷新令牌无效或已过期");

    /// <summary>
    /// 记录一次登录失败并构造统一凭据错误异常。
    /// </summary>
    /// <param name="clientIp">客户端 IP。</param>
    /// <param name="username">已归一化的用户名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>错误码 <c>1100</c> 的业务异常。</returns>
    /// <remarks>
    /// 把"计数 + 记日志 + 写审计 + 造异常"收在一处，是为了让两条失败路径<b>无法</b>长得不一样——
    /// 一旦有人在其中一条加了额外的日志或不同的错误码，用户名就能被枚举出来。
    /// </remarks>
    private async Task<BusinessException> FailLoginAsync(
        string clientIp,
        string username,
        CancellationToken cancellationToken)
    {
        var failures = _attemptTracker.RecordFailure(clientIp, username);

        _logger.LogWarning(
            "登录失败（IP={ClientIp}，用户名={Username}，窗口内失败次数={Failures}）",
            clientIp,
            username,
            failures);

        // 全量记录（用户已确认）：可被暴力破解刷表的风险由上面的限流兜住——
        // 同一 IP + 用户名每分钟最多 5 次，超出那一次记 Denied / 1103。
        await this.RecordAuthenticationAsync(
            AuditActions.Login,
            AuditOutcome.Failure,
            username,
            actorId: null,
            ErrorCodes.Unauthenticated,
            clientIp,
            cancellationToken,
            detail: $"窗口内失败 {failures} 次").ConfigureAwait(false);

        return BusinessException.Unauthenticated(InvalidCredentialsMessage);
    }

    /// <summary>
    /// 写一条认证类审计。
    /// </summary>
    /// <param name="action">动作标识。</param>
    /// <param name="outcome">结果。</param>
    /// <param name="actorName">操作者名（登录时是<b>尝试的用户名</b>）。</param>
    /// <param name="actorId">操作者主键；未认证时为 <see langword="null"/>。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="clientIp">来源 IP。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="detail">附加说明。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>为什么显式传 actorName</b>：登录/刷新/登出这些路径上
    /// <c>ICurrentUser</c> 可能是匿名的（登录失败时尤其如此），
    /// 审计必须记下**当时尝试的那个用户名**，而不是请求上下文里的身份。
    /// </remarks>
    private Task RecordAuthenticationAsync(
        string action,
        AuditOutcome outcome,
        string actorName,
        Guid? actorId,
        int? errorCode,
        string? clientIp,
        CancellationToken cancellationToken,
        string? detail = null) =>
        _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Authentication,
                Action = action,
                Outcome = outcome,
                ActorId = actorId,
                ActorName = actorName,
                ErrorCode = errorCode,
                ClientIp = clientIp,
                Detail = detail,
            },
            cancellationToken);
}
