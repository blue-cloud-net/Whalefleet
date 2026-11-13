using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Auth;
using Whalefleet.Core.Users;

namespace Whalefleet.Application.Sessions;

/// <summary>
/// 会话校验器（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>判定顺序有讲究</b>：先看会话本身（不存在 / 已吊销 / 已过期），
/// 再看账号状态（停用 / 必须改密）。
/// 「会话已吊销」比「账号已停用」更基础——停用账号时本就会撤销它的全部会话，
/// 因此两者同时成立时先报「会话失效」也对得上用户看到的现象（被踢下线）。</para>
/// <para><b>缓存命中就不查库</b>：这是本类的全部性能意义所在。
/// 未命中时一次查询把会话与用户状态一起取回（两次往返），再写入缓存。</para>
/// </remarks>
public sealed class SessionValidator : ISessionValidator
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IUserRepository _users;
    private readonly ISessionStatusCache _cache;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 初始化会话校验器。
    /// </summary>
    /// <param name="refreshTokens">刷新令牌仓储（会话行的来源）。</param>
    /// <param name="users">用户仓储（账号状态的来源）。</param>
    /// <param name="cache">会话状态缓存。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public SessionValidator(
        IRefreshTokenRepository refreshTokens,
        IUserRepository users,
        ISessionStatusCache cache,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _refreshTokens = refreshTokens;
        _users = users;
        _cache = cache;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<SessionValidationResult> ValidateAsync(
        Guid sessionId,
        string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            return new SessionValidationResult(SessionOutcome.NotFound);
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var cached = _cache.Get(sessionId);
        if (cached is not null)
        {
            return Evaluate(cached, utcNow);
        }

        var token = await _refreshTokens
            .QueryByIdAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            // 会话不存在：也缓存一份「不存在」的快照，避免每次都用同一个废令牌打一次库。
            _cache.Set(new SessionState(
                sessionId,
                Guid.Empty,
                DateTime.MinValue,
                Exists: false,
                IsRevoked: false,
                IsDisabled: false,
                MustChangePassword: false,
                Fingerprint: null));

            return new SessionValidationResult(SessionOutcome.NotFound);
        }

        var user = await _users
            .QueryByIdAsync(token.UserId, cancellationToken)
            .ConfigureAwait(false);

        var state = new SessionState(
            token.Id,
            token.UserId,
            token.ExpiresAt,
            Exists: true,
            IsRevoked: token.RevokedAt is not null,
            IsDisabled: user?.IsDisabled ?? true,
            MustChangePassword: user?.MustChangePassword ?? false,
            token.DeviceFingerprint);

        _cache.Set(state);

        return Evaluate(state, utcNow);
    }

    /// <summary>
    /// 按状态快照得出结论。
    /// </summary>
    /// <param name="state">会话状态快照。</param>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <returns>校验结论。</returns>
    private static SessionValidationResult Evaluate(SessionState state, DateTime utcNow)
    {
        if (!state.Exists)
        {
            return new SessionValidationResult(SessionOutcome.NotFound);
        }

        if (state.IsRevoked)
        {
            return new SessionValidationResult(SessionOutcome.Revoked, state.UserId);
        }

        if (state.IsExpired(utcNow))
        {
            return new SessionValidationResult(SessionOutcome.Expired, state.UserId);
        }

        if (state.IsDisabled)
        {
            return new SessionValidationResult(SessionOutcome.AccountDisabled, state.UserId);
        }

        if (state.MustChangePassword)
        {
            return SessionValidationResult.Valid(state.UserId, state.Fingerprint, mustChangePassword: true);
        }

        return SessionValidationResult.Valid(state.UserId, state.Fingerprint);
    }
}
