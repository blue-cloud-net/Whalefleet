using Whalefleet.Api.Security;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Auth;
using Whalefleet.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 认证端点（全局作用域，路径不含 <c>hostId</c>）。
/// </summary>
/// <remarks>
/// <para><b>失败路径不在这里处理</b>：凭据错误、限流等一律由 <c>AuthService</c> 抛
/// <see cref="BusinessException"/>，交给全局异常中间件转成统一 <c>ApiResult</c>。
/// 控制器只负责"取输入 → 调用用例 → 包装成功响应"，这样错误码与 HTTP 状态码的映射
/// 只有一处（<c>ErrorHttpStatus</c>），不会出现某个端点自己算状态码的岔路。</para>
/// <para><b>本控制器不记录密码</b>：日志只写用户名与来源 IP。</para>
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AuthController> _logger;

    /// <summary>
    /// 初始化认证控制器。
    /// </summary>
    /// <param name="authService">认证用例服务。</param>
    /// <param name="currentUser">当前登录用户（由 JWT claims 解析）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AuthController(
        AuthService authService,
        ICurrentUser currentUser,
        ILogger<AuthController> logger)
    {
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(logger);

        _authService = authService;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// 用户名 / 密码登录。
    /// </summary>
    /// <param name="request">登录请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为 <see cref="LoginResponse"/>。</returns>
    /// <response code="200">登录成功。</response>
    /// <response code="400">用户名或密码为空（<c>1000</c>）。</response>
    /// <response code="401">用户名或密码错误（<c>1100</c>，两种情况响应完全一致）。</response>
    /// <response code="429">失败次数超限（<c>1103</c>）。</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResult<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var clientIp = ClientIpResolver.Resolve(this.HttpContext);

        var response = await _authService
            .LoginAsync(request, clientIp, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("登录成功（用户名={Username}，IP={ClientIp}）", response.User.Username, clientIp);

        return this.Ok(ApiResults.Ok(response));
    }

    /// <summary>
    /// 用刷新令牌换取新的令牌对（<b>轮换</b>）。
    /// </summary>
    /// <param name="request">含刷新令牌的请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新的 <see cref="LoginResponse"/>。</returns>
    /// <response code="200">刷新成功（旧刷新令牌已作废）。</response>
    /// <response code="400">未提供 refreshToken（<c>1000</c>）。</response>
    /// <response code="401">令牌无效 / 已撤销 / 已过期（<c>1102</c>）。</response>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResult<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _authService
            .RefreshAsync(request.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(response));
    }

    /// <summary>
    /// 登出：撤销当前刷新令牌。
    /// </summary>
    /// <param name="request">含刷新令牌的请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应（无 <c>data</c> 字段）。</returns>
    /// <remarks>
    /// <b>幂等</b>：令牌不存在或已撤销同样返回成功。
    /// 注意本端点<b>不</b>使 access token 失效——它在到期前仍然有效，
    /// 因此客户端登出时必须同时清除本地的 access token。
    /// </remarks>
    /// <response code="200">登出成功（含令牌已失效的情况）。</response>
    /// <response code="400">未提供 refreshToken（<c>1000</c>）。</response>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LogoutAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        await _authService.LogoutAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 获取当前登录用户摘要。
    /// </summary>
    /// <returns>统一响应，<c>data</c> 为 <see cref="AuthenticatedUser"/>。</returns>
    /// <remarks>
    /// 前端在刷新页面后用本端点校验持久化的 access token 是否<b>仍然有效</b>
    /// （签名、过期、角色以服务端为准），比前端自行解码 JWT 更可靠。
    /// 它同时是验证「401 / 403 也会返回统一 <c>ApiResult</c>」这一契约的受保护端点。
    /// </remarks>
    /// <response code="200">返回当前用户。</response>
    /// <response code="401">未携带 Token（<c>1100</c>）或 Token 已过期（<c>1101</c>）。</response>
    [HttpPost("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResult<AuthenticatedUser>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public IActionResult GetCurrentUser()
    {
        var userId = _currentUser.UserId;
        var username = _currentUser.Username;
        var role = _currentUser.Role;

        // [Authorize] 已保证请求通过鉴权，因此走到这里的令牌必然由本项目签发。
        // 仍做一次显式检查：宁可回 401，也不要让一个残缺的声明集变成下游的空引用。
        if (userId is null || String.IsNullOrEmpty(username) || String.IsNullOrEmpty(role))
        {
            _logger.LogWarning("已通过鉴权的请求缺少必要的用户声明，可能来自旧密钥签发的令牌");

            throw BusinessException.Unauthenticated("令牌缺少必要的用户声明");
        }

        return this.Ok(ApiResults.Ok(new AuthenticatedUser(userId.Value, username, role)));
    }
}
