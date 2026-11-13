namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 会话状态快照（0.13.0）。
/// </summary>
/// <param name="SessionId">会话 id。</param>
/// <param name="UserId">会话所属用户主键。</param>
/// <param name="ExpiresAt">会话过期时间（UTC）。</param>
/// <param name="Exists">会话行是否存在（不存在时其余字段无意义）。</param>
/// <param name="IsRevoked">会话是否已被吊销。</param>
/// <param name="IsDisabled">所属账号是否已停用。</param>
/// <param name="MustChangePassword">所属账号是否必须先改密。</param>
/// <param name="Fingerprint">最近一次上报的设备指纹（仅展示用）。</param>
/// <remarks>
/// <para><b>它是「查询结果」而不是「凭据」</b>：不含任何可用来冒用会话的秘密
/// （令牌哈希不在这份快照里）。因此它可以安全地放进进程内缓存。</para>
/// <para><b>为什么缓存它</b>：每个认证请求都要确认「这个会话还活着吗」，
/// 不缓存就是每个请求一次数据库查询。缓存 TTL 60 秒，撤销与停用时主动失效。</para>
/// </remarks>
public sealed record SessionState(
    Guid SessionId,
    Guid UserId,
    DateTime ExpiresAt,
    bool Exists,
    bool IsRevoked,
    bool IsDisabled,
    bool MustChangePassword,
    string? Fingerprint)
{
    /// <summary>
    /// 判断会话在给定时刻是否已过期。
    /// </summary>
    /// <param name="utcNow">当前时间（UTC）。</param>
    /// <returns>已过期返回 <see langword="true"/>。</returns>
    public bool IsExpired(DateTime utcNow) => this.Exists && utcNow >= this.ExpiresAt;
}

/// <summary>
/// 会话状态缓存（0.13.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么缓存</b>：会话校验在每个认证请求上都要跑一次。
/// 不缓存的话，它会把「每个请求一次数据库查询」变成常态——
/// 而权限判定那侧已经是请求级缓存，两者叠加会让自托管实例的 SQLite 压力明显上升。</para>
/// <para><b>TTL 60 秒</b>：撤销与停用时<b>主动移除</b>对应项，因此单实例下即时生效；
/// 多实例部署没有共享缓存，最坏 60 秒收敛——这是刻意的取舍（见
/// <c>docs/api-spec.md</c> 的会话管理小节）。</para>
/// <para><b>接口放在 <c>Application/Abstractions</c></b>：它是「进程内缓存」这一外部资源的访问口，
/// 实现（<c>IMemoryCache</c>）在 Infrastructure。</para>
/// </remarks>
public interface ISessionStatusCache
{
    /// <summary>
    /// 读取会话状态快照。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <returns>命中返回快照；未命中返回 <see langword="null"/>。</returns>
    SessionState? Get(Guid sessionId);

    /// <summary>
    /// 写入会话状态快照。
    /// </summary>
    /// <param name="state">会话状态快照。</param>
    void Set(SessionState state);

    /// <summary>
    /// 移除单个会话的缓存项（吊销 / 停用 / 改密时调用）。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    void Remove(Guid sessionId);

    /// <summary>
    /// 移除某个用户<b>全部</b>会话的缓存项（全部吊销 / 停用 / 改密时调用）。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    void RemoveByUser(Guid userId);
}
