using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Auth;
using Docker.Manager.Core.Users;
using Microsoft.Extensions.Logging;

namespace Docker.Manager.Application.Auth;

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
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ILoginAttemptTracker _attemptTracker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthService> _logger;

    /// <summary>
    /// 初始化 <see cref="AuthService"/>。
    /// </summary>
    /// <param name="users">用户仓储。</param>
    /// <param name="roles">角色仓储。</param>
    /// <param name="refreshTokens">刷新令牌仓储。</param>
    /// <param name="passwordHasher">密码哈希器。</param>
    /// <param name="tokenService">令牌服务。</param>
    /// <param name="attemptTracker">登录失败计数器。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AuthService(
        IUserRepository users,
        IRoleRepository roles,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ILoginAttemptTracker attemptTracker,
        TimeProvider timeProvider,
        ILogger<AuthService> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(attemptTracker);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _users = users;
        _roles = roles;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _attemptTracker = attemptTracker;
        _timeProvider = timeProvider;
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

            throw new BusinessException(ErrorCodes.LoginRateLimited, "登录尝试过于频繁，请稍后再试");
        }

        var user = await _users.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            // 时序对齐：没有这个用户，也要付出一次等价的哈希计算成本。
            _passwordHasher.PerformDummyVerification(password);

            throw this.FailLogin(clientIp, username);
        }

        if (!_passwordHasher.Verify(password, user.PasswordHash))
        {
            throw this.FailLogin(clientIp, username);
        }

        var role = await _roles.QueryByIdAsync(user.RoleId, cancellationToken).ConfigureAwait(false);

        if (role is null)
        {
            // 外键保证用户必然指向一个存在的角色；走到这里说明数据被手工破坏。
            _logger.LogError(
                "用户 {Username} 的角色 {RoleId} 不存在，无法登录",
                user.Username,
                user.RoleId);

            throw BusinessException.Internal("用户所属角色不存在");
        }

        _attemptTracker.Reset(clientIp, username);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var accessToken = _tokenService.CreateAccessToken(user.Id, user.Username, role.Name, utcNow);
        var refreshToken = _tokenService.CreateRefreshToken(utcNow);

        await _refreshTokens.AddAsync(
            RefreshToken.Issue(Guid.NewGuid(), user.Id, refreshToken.Hash, refreshToken.ExpiresAt),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("用户 {Username} 登录成功（IP={ClientIp}）", user.Username, clientIp);

        return new LoginResponse(
            accessToken.Value,
            refreshToken.Raw,
            (int)Math.Max(0, (accessToken.ExpiresAt - utcNow).TotalSeconds),
            new AuthenticatedUser(user.Id, user.Username, role.Name));
    }

    /// <summary>
    /// 记录一次登录失败并构造统一凭据错误异常。
    /// </summary>
    /// <param name="clientIp">客户端 IP。</param>
    /// <param name="username">已归一化的用户名。</param>
    /// <returns>错误码 <c>1100</c> 的业务异常。</returns>
    /// <remarks>
    /// 把"计数 + 记日志 + 造异常"收在一处，是为了让两条失败路径<b>无法</b>长得不一样——
    /// 一旦有人在其中一条加了额外的日志或不同的错误码，用户名就能被枚举出来。
    /// </remarks>
    private BusinessException FailLogin(string clientIp, string username)
    {
        var failures = _attemptTracker.RecordFailure(clientIp, username);

        _logger.LogWarning(
            "登录失败（IP={ClientIp}，用户名={Username}，窗口内失败次数={Failures}）",
            clientIp,
            username,
            failures);

        return BusinessException.Unauthenticated(InvalidCredentialsMessage);
    }
}
