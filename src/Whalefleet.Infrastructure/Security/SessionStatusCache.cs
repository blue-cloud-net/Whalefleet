using Microsoft.Extensions.Caching.Memory;

using Whalefleet.Application.Abstractions;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 会话状态缓存的 <see cref="IMemoryCache"/> 实现（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>TTL 60 秒</b>：撤销与停用时由调用方<b>主动移除</b>对应项，
/// 因此单实例下即时生效；多实例部署没有共享缓存，最坏 60 秒收敛——
/// 这是刻意的取舍（自托管单实例是主要场景，换来的是不必引入 Redis 依赖）。</para>
/// <para><b>为什么还要维护「用户 → 会话」索引</b>：撤销一个用户的<b>全部</b>会话时
/// 需要清掉他所有会话的缓存项。没有索引就只能遍历整份缓存（键数量不可控），
/// 而有了索引就是一次 O(该用户会话数) 的删除。</para>
/// <para><b>索引的清理</b>：会话过期或被移除时要从索引里摘掉，
/// 否则索引会随时间无限增长——它比缓存本身更值得注意。</para>
/// </remarks>
internal sealed class SessionStatusCache : ISessionStatusCache
{
    /// <summary>
    /// 缓存项存活时长（秒）。
    /// </summary>
    /// <remarks>
    /// 撤销 / 停用 / 改密都会主动移除对应项，因此 TTL 只影响「别处发起的变更」的收敛速度。
    /// </remarks>
    private const int _ttlSeconds = 60;

    private static readonly string _userIndexKeyPrefix = "session-user:";

    private readonly IMemoryCache _cache;

    /// <summary>
    /// 初始化缓存。
    /// </summary>
    /// <param name="cache">进程内缓存。</param>
    public SessionStatusCache(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
    }

    /// <inheritdoc />
    public SessionState? Get(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        return _cache.TryGetValue(BuildKey(sessionId), out SessionState? state) ? state : null;
    }

    /// <inheritdoc />
    public void Set(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SessionId == Guid.Empty)
        {
            return;
        }

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_ttlSeconds),
        };

        _cache.Set(BuildKey(state.SessionId), state, options);

        if (state.UserId != Guid.Empty)
        {
            this.AddToUserIndex(state.UserId, state.SessionId);
        }
    }

    /// <inheritdoc />
    public void Remove(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }

        if (_cache.TryGetValue(BuildKey(sessionId), out SessionState? state) && state is not null)
        {
            this.RemoveFromUserIndex(state.UserId, sessionId);
        }

        _cache.Remove(BuildKey(sessionId));
    }

    /// <inheritdoc />
    public void RemoveByUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return;
        }

        var indexKey = BuildUserIndexKey(userId);

        if (_cache.TryGetValue(indexKey, out HashSet<Guid>? sessions) && sessions is not null)
        {
            foreach (var sessionId in sessions)
            {
                _cache.Remove(BuildKey(sessionId));
            }
        }

        _cache.Remove(indexKey);
    }

    /// <summary>
    /// 构造单个会话的缓存键。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <returns>缓存键。</returns>
    private static string BuildKey(Guid sessionId) => $"session:{sessionId:D}";

    /// <summary>
    /// 构造「用户 → 会话」索引的缓存键。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <returns>缓存键。</returns>
    private static string BuildUserIndexKey(Guid userId) => $"{_userIndexKeyPrefix}{userId:D}";

    /// <summary>
    /// 把会话登记到用户的索引里。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="sessionId">会话 id。</param>
    private void AddToUserIndex(Guid userId, Guid sessionId)
    {
        var indexKey = BuildUserIndexKey(userId);

        var sessions = _cache.TryGetValue(indexKey, out HashSet<Guid>? existing) && existing is not null
            ? existing
            : new HashSet<Guid>();

        sessions.Add(sessionId);

        // 索引的 TTL 要比单条会话长一点：否则会话还在、索引却先过期，
        // 「撤销该用户全部会话」就会漏掉它。
        _cache.Set(
            indexKey,
            sessions,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_ttlSeconds * 3) });
    }

    /// <summary>
    /// 从用户的索引里摘掉某个会话。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="sessionId">会话 id。</param>
    private void RemoveFromUserIndex(Guid userId, Guid sessionId)
    {
        if (userId == Guid.Empty)
        {
            return;
        }

        var indexKey = BuildUserIndexKey(userId);

        if (!_cache.TryGetValue(indexKey, out HashSet<Guid>? sessions) || sessions is null)
        {
            return;
        }

        sessions.Remove(sessionId);

        if (sessions.Count == 0)
        {
            _cache.Remove(indexKey);
            return;
        }

        _cache.Set(
            indexKey,
            sessions,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_ttlSeconds * 3) });
    }
}
