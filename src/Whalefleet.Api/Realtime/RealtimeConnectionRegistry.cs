namespace Whalefleet.Api.Realtime;

/// <summary>
/// 在线连接 → 登录用户的映射（Singleton）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：容器事件是<b>全局</b>的（通知 Hub 的路径里没有 <c>hostId</c>），
/// 而「谁能看见某台主机上的事件」取决于该用户的<b>主机级角色分配</b>。
/// 推之前必须逐个连接判权限，因此需要一个「连接 → 用户」的映射。</para>
/// <para><b>为什么不用 SignalR 自带的 <c>Clients.Group</c></b>：按主机分组需要知道
/// 「哪些用户对这台主机有权限」，而权限是<b>运行时可变</b>的（角色改完即时生效）——
/// 分组在连接建立时固定，改权限后不会重新分组。按连接逐个判虽然多一次查库，
/// 但那正是「权限改动即时生效」的代价，也是本项目 0.1.0 刻意选的路子。</para>
/// <para><b>为什么是 Singleton</b>：连接是跨请求存在的，映射表同理。</para>
/// </remarks>
public sealed class RealtimeConnectionRegistry
{
    /// <summary>
    /// 连接 → 用户的映射。
    /// </summary>
    private readonly Dictionary<string, Guid> _connections = new(StringComparer.Ordinal);

    /// <summary>
    /// 保护映射表的锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 登记一条连接。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="userId">登录用户主键。</param>
    public void Add(string connectionId, Guid userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (this._gate)
        {
            this._connections[connectionId] = userId;
        }
    }

    /// <summary>
    /// 移除一条连接。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    public void Remove(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (this._gate)
        {
            this._connections.Remove(connectionId);
        }
    }

    /// <summary>
    /// 取出全部在线连接及其用户。
    /// </summary>
    /// <returns>连接标识与用户主键的列表（快照）。</returns>
    /// <remarks>
    /// 返回<b>快照</b>而不是在线引用：判权限要查库，耗时不可忽略，
    /// 而期间连接可能已经断开。拿着快照去推，推给已断开的连接只会得到一个可忽略的异常。
    /// </remarks>
    public IReadOnlyList<(string ConnectionId, Guid UserId)> Snapshot()
    {
        lock (this._gate)
        {
            return [.. this._connections.Select(pair => (pair.Key, pair.Value))];
        }
    }
}
