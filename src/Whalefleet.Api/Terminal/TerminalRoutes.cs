namespace Whalefleet.Api.Terminal;

/// <summary>
/// 交互式终端的路径常量。
/// </summary>
/// <remarks>
/// <para><b>为什么集中成常量</b>：<c>/ws</c> 前缀同时出现在四处的<b>判断</b>里——
/// 端点映射、SPA 深链回退的排除列表、当前主机解析的排除判断，以及反代示例文档。
/// 各处各写一遍字面量，改一处忘一处的症状是「终端连不上但日志里全是 200」
/// （回退中间件把响应写成 index.html）或「一开终端就 403」（当前主机解析拿匿名请求去查库）。</para>
/// <para><b>为什么不走 SignalR</b>：终端是<b>原始双向字节流</b>，而 Hub 的模型是
/// 「方法调用 + 事件推送」。硬塞进 Hub 需要为每个会话维护一个方法名与参数编码，
/// 反而比直接读写 <c>WebSocket</c> 多一层。0.5.0 的三条 Hub 也因此保持不动。</para>
/// </remarks>
public static class TerminalRoutes
{
    /// <summary>
    /// 终端长连接的路径前缀。
    /// </summary>
    public const string Prefix = "/ws";

    /// <summary>
    /// 容器 exec 终端的路径模板。
    /// </summary>
    /// <remarks>
    /// <c>hostId</c> 在路径里（与项目「主机进路径」的约定一致），但它<b>只用于可读性与对账</b>——
    /// 真正的授权事实来自令牌：令牌里绑了主机、容器与命令，路径里的主机与令牌不一致时直接拒。
    /// </remarks>
    public const string ContainerExec = Prefix + "/hosts/{hostId}/containers/exec";

    /// <summary>
    /// 查询参数名：terminal token。
    /// </summary>
    /// <remarks>
    /// <b>只接受这一个参数</b>：容器与命令都在令牌里，因此客户端没有"改目标"的余地。
    /// 早期草案里的 <c>?containerId=</c> / <c>?cmd=</c> 已废弃——它们让令牌退化成一张门禁卡。
    /// </remarks>
    public const string TokenQueryKey = "token";

    /// <summary>
    /// 查询参数名：初始列数。
    /// </summary>
    public const string ColumnsQueryKey = "cols";

    /// <summary>
    /// 查询参数名：初始行数。
    /// </summary>
    public const string RowsQueryKey = "rows";
}
