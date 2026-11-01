namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排 Stack 列表请求。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态。
/// 校验与夹紧统一在 <see cref="StackService.ListAsync"/> 里做。
/// </remarks>
public sealed record ListStacksRequest
{
    /// <summary>
    /// 关键字搜索：按 Stack 名称模糊匹配（不区分大小写）。
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// 状态筛选：<c>running</c> / <c>partial</c> / <c>stopped</c> / <c>unknown</c>；
    /// 省略表示不过滤。
    /// </summary>
    /// <remarks>
    /// <b>筛选在服务端做</b>：分页也是服务端切片，
    /// 前端过滤只能筛到当前页——页脚会显示一个与翻页结果对不上的数字。
    /// </remarks>
    public string? Status { get; init; }

    /// <summary>
    /// 页码（从 0 开始）；省略时按 0 处理，负数同样按 0 处理。
    /// </summary>
    public int? PageIndex { get; init; }

    /// <summary>
    /// 每页条数；省略时用默认值（20），超出上限（100）会被夹紧。
    /// </summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 编排 Stack 详情请求。
/// </summary>
public sealed record InspectStackRequest
{
    /// <summary>
    /// Stack 名称；必填。
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>
/// 部署 / 更新编排 Stack 的请求。
/// </summary>
/// <remarks>
/// <b>校验在请求期完成，执行在后台</b>：语法错、重名、非本平台部署这些问题
/// 在 HTTP 响应里直接报出来，用户改一下 YAML 就能重来，不用等一个后台任务跑完。</remarks>
public sealed record ApplyStackRequest
{
    /// <summary>
    /// Stack 名称；必填。须符合 Compose 项目名规则。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Compose 文件原文（YAML）；必填。
    /// </summary>
    public string? ComposeContent { get; init; }

    /// <summary>
    /// 任务标识（客户端生成的进度令牌）；必填。
    /// </summary>
    /// <remarks>
    /// <b>为什么由客户端生成</b>：与镜像拉取进度同一做法——
    /// 服务端没法知道「这次部署对应哪条 Hub 连接」。
    /// 客户端先生成令牌、订阅它、再把它带进请求，三者由此串起来。
    /// </remarks>
    public string? TaskId { get; init; }

    /// <summary>
    /// 是否总是拉取最新镜像；省略为 <see langword="false"/>（只在本地缺失时拉）。
    /// </summary>
    /// <remarks>
    /// <b>为 <c>true</c> 时拉取失败即整任务失败</b>：不静默回退到本地旧镜像——
    /// 用户点了「拉取最新」而实际跑的仍是旧版本，是最糟的组合。
    /// </remarks>
    public bool? PullAlways { get; init; }

    /// <summary>
    /// 是否清掉「已不在 YAML 里的」孤儿服务；更新时省略为 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// <b>数据卷不在清理范围内</b>：卷里是用户的数据，
    /// 删它的后果不可逆，而它可以事后单独清理。
    /// </remarks>
    public bool? PruneOrphans { get; init; }
}

/// <summary>
/// 停止编排 Stack 的请求。
/// </summary>
public sealed record StopStackRequest
{
    /// <summary>
    /// Stack 名称；必填。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 任务标识（客户端生成的进度令牌）；必填。
    /// </summary>
    public string? TaskId { get; init; }
}

/// <summary>
/// 删除编排 Stack 的请求。
/// </summary>
public sealed record RemoveStackRequest
{
    /// <summary>
    /// Stack 名称；必填。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 任务标识（客户端生成的进度令牌）；必填。
    /// </summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// 是否连数据卷一起删；省略为 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// <b>默认保留</b>：删除 Stack 是个常规动作，而误删数据卷不可恢复。
    /// 界面上的删除对话框必须让用户显式勾选这一项。
    /// </remarks>
    public bool? RemoveVolumes { get; init; }
}

/// <summary>
/// 查询编排任务快照的请求。
/// </summary>
public sealed record GetStackTaskRequest
{
    /// <summary>
    /// 任务标识；必填。
    /// </summary>
    public string? TaskId { get; init; }
}

/// <summary>
/// 轮换 / 吊销 Webhook 令牌的请求。
/// </summary>
public sealed record StackWebhookRequest
{
    /// <summary>
    /// Stack 名称；必填。
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>
/// 由 Webhook 触发的重放请求。
/// </summary>
public sealed record StackWebhookReloadRequest
{
    /// <summary>
    /// 是否总是拉取最新镜像；省略为 <see langword="false"/>。
    /// </summary>
    public bool? PullAlways { get; init; }

    /// <summary>
    /// 是否清掉孤儿服务；省略为 <see langword="true"/>。
    /// </summary>
    public bool? PruneOrphans { get; init; }

    /// <summary>
    /// 是否等待任务结束再返回；省略为 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// <b>同步等待有上限</b>：CI 通常需要「部署完再退出」，
    /// 但无限等待会被反代掐断（nginx 默认读超时 60s）。
    /// 因此等待有上限，超时后返回 202 + 任务快照，CI 可改用轮询。
    /// </remarks>
    public bool? Wait { get; init; }
}

/// <summary>
/// 查询 Webhook 任务快照的请求。
/// </summary>
public sealed record StackWebhookTaskRequest
{
    /// <summary>
    /// 任务标识；必填。
    /// </summary>
    public string? TaskId { get; init; }
}
