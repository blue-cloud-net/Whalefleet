namespace Docker.Manager.Application.Containers;

/// <summary>
/// 容器查询请求。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态。校验统一在 <c>ContainerService</c> 里做并给出可读消息。
/// </remarks>
public sealed record ListContainersRequest
{
    /// <summary>是否包含已停止的容器；省略时只列运行中的（与 <c>docker ps</c> 一致）。</summary>
    public bool? All { get; init; }

    /// <summary>页码（从 0 开始）；省略时按 0 处理。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数；省略时用默认值，超出上限会被夹紧。</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 容器详情请求。
/// </summary>
public sealed record InspectContainerRequest
{
    /// <summary>容器标识（完整 ID 或名称）。</summary>
    public string? ContainerId { get; init; }
}

/// <summary>
/// 容器日志请求。
/// </summary>
/// <remarks>
/// 0.0.9 一次性返回日志文本；逐行实时流走 SignalR（0.3.0）。因此这里刻意<b>不</b>暴露
/// <c>Follow</c>——一次性 HTTP 响应跟着流没有意义，只会让连接挂住。
/// </remarks>
public sealed record ContainerLogsRequest
{
    /// <summary>容器标识。</summary>
    public string? ContainerId { get; init; }

    /// <summary>只取最后 N 行；省略时取全部（与 <c>docker logs</c> 一致）。</summary>
    public int? Tail { get; init; }

    /// <summary>只取该时刻（含）之后的日志，Unix 秒。</summary>
    public long? Since { get; init; }

    /// <summary>是否在每行前加时间戳。</summary>
    public bool? Timestamps { get; init; }

    /// <summary>是否包含 stderr；省略时包含（用户要看的就是全部输出）。</summary>
    public bool? IncludeStderr { get; init; }
}
