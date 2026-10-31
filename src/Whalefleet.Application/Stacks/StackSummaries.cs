namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排 Stack 列表项。
/// </summary>
/// <param name="Name">Stack 名称（= Compose 项目名）。</param>
/// <param name="Managed">是否由本平台部署。</param>
/// <param name="Status">整体状态（<c>running</c> / <c>partial</c> / <c>stopped</c> / <c>unknown</c>）。</param>
/// <param name="ServiceCount">服务数。</param>
/// <param name="ContainerCount">容器数（含已停止的）。</param>
/// <param name="RunningCount">在运行的容器数。</param>
/// <param name="NetworkCount">网络数。</param>
/// <param name="VolumeCount">具名卷数。</param>
/// <param name="ComposeAvailable">本平台是否保存着这个项目的 YAML。</param>
/// <param name="WebhookEnabled">是否启用了 Webhook。</param>
/// <param name="WebhookTokenHint">Webhook 令牌提示（明文的前若干位）。</param>
/// <param name="WebhookLastUsedAt">Webhook 最近一次触发时间（UTC）。</param>
/// <param name="UpdatedAt">最后修改时间（UTC）。</param>
/// <remarks>
/// <para><b><see cref="Managed"/> 是本列表最重要的字段</b>：它区分
/// 「这个项目是本平台部署的（我们有它的 YAML，能更新）」与
/// 「这个项目是用户自己在命令行 <c>docker compose up</c> 的（没有 YAML，只能停 / 删）」。
/// 界面上必须把它显示出来——否则用户会对一个外部项目点「更新」，
/// 而那必然失败。</para>
/// <para><b><see cref="Status"/> 的三档</b>：<c>running</c>（全部在跑）、
/// <c>partial</c>（部分在跑）、<c>stopped</c>（全停）、
/// <c>unknown</c>（连不上或没有容器）。之所以要有 <c>partial</c> 这一档：
/// 「部分服务没起来」是编排最常见的问题，而只有 running / stopped 两档时它会被误报成 stopped。</para>
/// <para><b>没有 <c>Id</c></b>：Stack 用<b>名称</b>标识（它就是 Compose 项目名），
/// 与主机名、卷名一样。数据库里的 Guid 只是内部主键，不对外暴露。</para>
/// </remarks>
public sealed record StackSummary(
    string Name,
    bool Managed,
    string Status,
    int ServiceCount,
    int ContainerCount,
    int RunningCount,
    int NetworkCount,
    int VolumeCount,
    bool ComposeAvailable,
    bool WebhookEnabled,
    string? WebhookTokenHint,
    DateTime? WebhookLastUsedAt,
    DateTime? UpdatedAt);

/// <summary>
/// 编排 Stack 详情。
/// </summary>
/// <param name="Summary">列表项（同样的字段，便于前端复用）。</param>
/// <param name="ComposeContent">Compose 文件原文；本平台没保存时为 <see langword="null"/>。</param>
/// <param name="Services">服务列表。</param>
/// <param name="Warnings">解析期的提示。</param>
public sealed record StackDetail(
    StackSummary Summary,
    string? ComposeContent,
    IReadOnlyList<StackServiceSummary> Services,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 编排项目里的一个服务。
/// </summary>
/// <param name="Name">服务名。</param>
/// <param name="Image">镜像。</param>
/// <param name="ContainerId">容器 ID；该服务还没有容器时为 <see langword="null"/>。</param>
/// <param name="ContainerName">容器名；该服务还没有容器时为 <see langword="null"/>。</param>
/// <param name="State">容器状态（<c>running</c> / <c>exited</c> / <c>created</c> …）。</param>
/// <param name="Status">容器的状态文本（守护进程给的，如 <c>Up 3 minutes</c>）。</param>
/// <param name="Health">健康状态（<c>none</c> / <c>starting</c> / <c>healthy</c> / <c>unhealthy</c>）。</param>
/// <param name="Ports">端口映射。</param>
/// <param name="RestartPolicy">重启策略。</param>
public sealed record StackServiceSummary(
    string Name,
    string Image,
    string? ContainerId,
    string? ContainerName,
    string? State,
    string? Status,
    string? Health,
    IReadOnlyList<StackServicePort> Ports,
    string? RestartPolicy);

/// <summary>
/// 服务的一个端口映射。
/// </summary>
/// <param name="ContainerPort">容器内端口。</param>
/// <param name="Protocol">协议。</param>
/// <param name="HostPort">宿主端口；未绑定时为 <see langword="null"/>。</param>
/// <param name="HostIp">绑定的宿主地址。</param>
public sealed record StackServicePort(
    int ContainerPort,
    string Protocol,
    int? HostPort,
    string? HostIp);

/// <summary>
/// 编排 Stack 列表结果。
/// </summary>
/// <param name="Items">当前页的 Stack。</param>
/// <param name="Total">分页前的总条数。</param>
/// <param name="PageIndex">服务端实际使用的页码。</param>
/// <param name="PageSize">服务端实际使用的每页条数（已夹紧）。</param>
public sealed record StackListResult(
    IReadOnlyList<StackSummary> Items,
    int Total,
    int PageIndex,
    int PageSize);

/// <summary>
/// 编排任务的受理结果。
/// </summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="Status">任务当前状态。</param>
/// <param name="QueuePosition">队列中的位置；<c>0</c> 表示正在执行。</param>
/// <remarks>
/// <b>为什么立刻返回而不是等任务跑完</b>：部署可能要拉镜像（几分钟），
/// HTTP 请求扛不住——反代会把连接掐掉，客户端拿到的是「504 网关超时」
/// 而不是「部署失败」。返回任务标识后，长耗时不再需要特殊处理。
/// </remarks>
public sealed record StackTaskAccepted(
    string TaskId,
    string Status,
    int QueuePosition);

/// <summary>
/// Webhook 令牌信息。
/// </summary>
/// <param name="Enabled">是否已启用。</param>
/// <param name="TokenHint">令牌提示（明文的前若干位）。</param>
/// <param name="Token">令牌明文；<b>只在生成 / 轮换的那一刻有值</b>，之后一律为 <see langword="null"/>。</param>
/// <param name="LastUsedAt">最近一次触发时间（UTC）。</param>
/// <remarks>
/// <para><b>明文只出现一次</b>：库里只存哈希，生成时把明文返回给界面，
/// 之后界面只能看到「令牌提示」（前几位）。
/// 界面上必须提示用户「请立刻保存，之后无法再查看」。</para>
/// </remarks>
public sealed record StackWebhookInfo(
    bool Enabled,
    string? TokenHint,
    string? Token,
    DateTime? LastUsedAt);
