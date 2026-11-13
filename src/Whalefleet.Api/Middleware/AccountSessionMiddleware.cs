using Whalefleet.Api.Terminal;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auth;

namespace Whalefleet.Api.Middleware;

/// <summary>
/// 会话校验中间件（0.13.0）：确认「这个登录态还能用吗」。
/// </summary>
/// <remarks>
/// <para><b>位置</b>：<c>UseAuthentication()</c> 之后、<c>UseAuthorization()</c> 之前。
/// <c>UseAuthentication</c> 已完成 JWT 签名与有效期校验并填充 <c>HttpContext.User</c>；
/// 本中间件只处理「令牌有效<b>但</b>会话已失效」与「账号已停用」——
/// 这两件事令牌本身看不出来。</para>
/// <para><b>为什么放在 <c>UseAuthorization</c> 之前</b>：这样连<b>匿名</b>端点与
/// <c>[Authorize]</c> 端点都能被统一拦截。若放在之后，权限策略会先跑一遍，
/// 而「会话已吊销」的用户仍会触发数据库查询与权限解析。</para>
/// <para><b>它不负责权限判定</b>：那是 <c>IPermissionChecker</c> 的事。
/// 本中间件只回答「这个人还能不能用这个登录态」。</para>
/// <para><b>跳过哪些请求</b>：① 未认证请求（匿名端点）；② <c>/ws</c> 前缀
/// （交互式终端用独立的 terminal token 鉴权，在本中间件眼里是匿名的）。</para>
/// </remarks>
public sealed class AccountSessionMiddleware
{
    /// <summary>
    /// 客户端上报设备指纹的请求头名。
    /// </summary>
    /// <remarks>
    /// <b>只记录、不校验</b>：指纹变化会被覆盖进会话记录，用于让人在会话列表里
    /// 认出「哪一条是我自己」——它<b>不是</b>安全边界，因此不构成拒绝理由。
    /// </remarks>
    public const string FingerprintHeader = "X-Client-Fingerprint";

    /// <summary>
    /// SignalR 握手用的指纹查询参数名（浏览器无法为 WebSocket 设置自定义请求头）。
    /// </summary>
    public const string FingerprintQuery = "fingerprint";

    /// <summary>
    /// 强制改密期间允许访问的路径（白名单）。
    /// </summary>
    /// <remarks>
    /// <b>为什么是这几个</b>：用户必须能查自己的身份（<c>auth/me</c>）、
    /// 能续期（<c>auth/refresh</c>，否则前端一进页面就被踢）、
    /// 能登出（<c>auth/logout</c>）以及能改密（<c>users/update-password</c>）。
    /// 少任何一个都会让用户卡在「必须改密」的状态里出不来。
    /// </remarks>
    private static readonly string[] _passwordChangeAllowedPaths =
    [
        "/api/v1/auth/me",
        "/api/v1/auth/refresh",
        "/api/v1/auth/logout",
        "/api/v1/admin/users/update-password",
    ];

    private readonly RequestDelegate _next;

    /// <summary>
    /// 初始化中间件。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// <b>构造函數里只放 <see cref="RequestDelegate"/></b>：<c>UseMiddleware</c> 在
    /// <b>应用启动时</b>用**根容器**构造中间件，从根容器解析 Scoped 服务会抛
    /// 「Cannot resolve scoped service from root provider」。
    /// Scoped 依赖因此改由 <see cref="InvokeAsync"/> 的参数注入——
    /// 框架会从 <c>HttpContext.RequestServices</c> 逐请求解析（与 <c>CurrentHostMiddleware</c> 同口径）。
    /// </remarks>
    public AccountSessionMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>
    /// 执行中间件。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="validator">会话校验器（Scoped）。</param>
    /// <param name="cleanupQueue">过期会话清理队列（Singleton）。</param>
    /// <param name="cache">会话状态缓存（Singleton）。</param>
    /// <param name="refreshTokens">刷新令牌仓储（Scoped；指纹覆盖写入）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 会话失效（<c>1100</c>）、账号已停用（<c>1108</c>）或必须先改密（<c>1205</c>）。
    /// </exception>
    public async Task InvokeAsync(
        HttpContext context,
        ISessionValidator validator,
        ISessionCleanupQueue cleanupQueue,
        ISessionStatusCache cache,
        IRefreshTokenRepository refreshTokens,
        ILogger<AccountSessionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(cleanupQueue);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(logger);

        if (!ShouldValidate(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var sessionId = ReadSessionId(context);

        if (sessionId is null)
        {
            // 没有 sid 的令牌一律拒绝：0.x 尚未正式发布，没有存量部署需要兼容。
            // 直接要求重新登录比「让旧令牌继续可用一段时间」更简单也更安全。
            throw new BusinessException(ErrorCodes.Unauthenticated, "登录态无效，请重新登录");
        }

        var fingerprint = ReadFingerprint(context);

        var result = await validator
            .ValidateAsync(sessionId.Value, fingerprint, context.RequestAborted)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case SessionOutcome.NotFound:
            case SessionOutcome.Revoked:
                throw new BusinessException(ErrorCodes.Unauthenticated, "登录态已失效，请重新登录");

            case SessionOutcome.Expired:
                // 过期会话顺手入队清理：不等每小时兜底那一轮。
                cleanupQueue.TryEnqueue(sessionId.Value);
                cache.Remove(sessionId.Value);
                throw new BusinessException(ErrorCodes.Unauthenticated, "登录态已过期，请重新登录");

            case SessionOutcome.AccountDisabled:
                throw new BusinessException(ErrorCodes.AccountDisabled, "账号已停用");

            case SessionOutcome.PasswordChangeRequired:
                if (!IsPasswordChangeAllowed(context))
                {
                    throw new BusinessException(
                        ErrorCodes.PasswordChangeRequired,
                        "必须先修改密码才能继续使用");
                }

                break;

            case SessionOutcome.Valid:
            default:
                break;
        }

        if (result.MustChangePassword && !IsPasswordChangeAllowed(context))
        {
            throw new BusinessException(
                ErrorCodes.PasswordChangeRequired,
                "必须先修改密码才能继续使用");
        }

        await RefreshFingerprintAsync(
                refreshTokens,
                cache,
                logger,
                sessionId.Value,
                fingerprint,
                result.Fingerprint,
                context.RequestAborted)
            .ConfigureAwait(false);

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// 指纹变化时<b>覆盖</b>会话记录（<b>不拒绝请求</b>）。
    /// </summary>
    /// <param name="refreshTokens">刷新令牌仓储。</param>
    /// <param name="cache">会话状态缓存。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="reported">客户端本次上报的指纹。</param>
    /// <param name="stored">会话记录里已有的指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>为什么写入失败不抛异常</b>：指纹只是给人看的辅助信息（认出「哪条会话是我自己」），
    /// 它<b>不是</b>安全边界。为一条辅助信息让请求失败显然不成比例。
    /// 写失败只记 Debug，下次请求还会再试（缓存里的指纹没变）。</remarks>
    private static async Task RefreshFingerprintAsync(
        IRefreshTokenRepository refreshTokens,
        ISessionStatusCache cache,
        ILogger<AccountSessionMiddleware> logger,
        Guid sessionId,
        string? reported,
        string? stored,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(reported))
        {
            return;
        }

        if (String.Equals(reported, stored, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await refreshTokens
                .UpdateFingerprintAsync(sessionId, reported, cancellationToken)
                .ConfigureAwait(false);

            // 同步缓存，否则下一分钟的每个请求都会重复写一次。
            var cached = cache.Get(sessionId);
            if (cached is not null)
            {
                cache.Set(cached with { Fingerprint = reported.Trim() });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "更新会话设备指纹失败（会话={SessionId}）", sessionId);
        }
    }

    /// <summary>
    /// 判断本次请求是否需要做会话校验。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>需要校验返回 <see langword="true"/>。</returns>
    private static bool ShouldValidate(HttpContext context)
    {
        // 未认证：匿名端点与尚未通过鉴权的请求。
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // /ws 用独立的 terminal token 鉴权，在本中间件眼里是匿名的。
        if (context.Request.Path.StartsWithSegments(TerminalRoutes.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从令牌声明里读出会话 id。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>会话 id；缺失或格式非法返回 <see langword="null"/>。</returns>
    private static Guid? ReadSessionId(HttpContext context)
    {
        var claim = context.User?.FindFirst(JwtClaimNames.SessionId)?.Value;

        if (String.IsNullOrWhiteSpace(claim))
        {
            return null;
        }

        return Guid.TryParse(claim, out var sessionId) ? sessionId : null;
    }

    /// <summary>
    /// 读取客户端上报的设备指纹（<b>只记录、不校验</b>）。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>指纹；未上报返回 <see langword="null"/>。</returns>
    private static string? ReadFingerprint(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(FingerprintHeader, out var headerValue)
            && !String.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString().Trim();
        }

        // SignalR 握手无法设置自定义请求头，退回查询参数。
        if (context.Request.Query.TryGetValue(FingerprintQuery, out var queryValue)
            && !String.IsNullOrWhiteSpace(queryValue))
        {
            return queryValue.ToString().Trim();
        }

        return null;
    }

    /// <summary>
    /// 判断当前路径是否在「强制改密」白名单里。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>在白名单里返回 <see langword="true"/>。</returns>
    private static bool IsPasswordChangeAllowed(HttpContext context)
    {
        var path = context.Request.Path.Value ?? String.Empty;

        return _passwordChangeAllowedPaths.Any(
            allowed => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
    }
}
