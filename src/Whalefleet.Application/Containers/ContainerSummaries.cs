namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器列表项。
/// </summary>
/// <param name="Id">容器完整 ID。</param>
/// <param name="Name">容器名（去掉 Docker 返回的前导斜杠）。</param>
/// <param name="Image">镜像名（如 <c>nginx:latest</c>）。</param>
/// <param name="ImageId">镜像 ID（<c>sha256:…</c>）。</param>
/// <param name="Command">启动命令。</param>
/// <param name="Created">创建时间（UTC）。</param>
/// <param name="State">状态（如 <c>running</c> / <c>exited</c>）。</param>
/// <param name="Status">人类可读状态（如 <c>Up 3 hours</c>）。</param>
/// <param name="Ports">端口映射，<b>已按确定性顺序排列</b>（见下）。</param>
/// <remarks>
/// <para><b>为什么逐字段挑选而不是直接序列化 SDK 的 <c>ContainerListResponse</c></b>：
/// 一是那个对象带着 <c>HostConfig</c> / <c>NetworkSettings</c> 等一大坨列表页用不到的东西，
/// 列表返回体会膨胀十几倍；二是 SDK 升级时字段变动会直接变成对外契约的变动。</para>
/// <para><b>端口数组的顺序是对外契约的一部分</b>：守护进程返回的
/// <c>Ports</c> 顺序每次调用都可能不同（Go 的 map 迭代顺序随机），
/// 因此服务端会显式排序——容器端口 ↑ → 协议 → 宿主端口（未映射的最后）→
/// 绑定地址等级（未指定/通配 → IPv4 → IPv6）→ 绑定地址。
/// 客户端可以直接依赖这个顺序，不必自己再排一次。</para>
/// <para><b>同一个端口可能有多条记录</b>：<c>-p 8080:80</c> 会被 Docker 记成
/// <c>0.0.0.0</c> 与 <c>::</c> 两条。是否合并成一行是<b>展示层</b>的决定。</para>
/// </remarks>
public sealed record ContainerSummary(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string Command,
    DateTimeOffset Created,
    string State,
    string Status,
    IReadOnlyList<ContainerPort> Ports);

/// <summary>
/// 端口映射。
/// </summary>
/// <param name="PrivatePort">容器内端口。</param>
/// <param name="PublicPort">宿主端口；未映射时为 <see langword="null"/>。</param>
/// <param name="Type">协议（<c>tcp</c> / <c>udp</c>）。</param>
/// <param name="Ip">绑定的宿主地址；未绑定时为 <see langword="null"/>。</param>
public sealed record ContainerPort(int PrivatePort, int? PublicPort, string Type, string? Ip);

/// <summary>
/// 容器列表的分页结果。
/// </summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数（分页前）。</param>
/// <param name="PageIndex">当前页码（从 0 开始）。</param>
/// <param name="PageSize">每页条数（夹紧后的实际值）。</param>
/// <remarks>
/// 分页在服务端内存里做：Docker API 不提供容器列表的分页参数。单台守护进程上容器数通常是几十到几百，
/// 一次拉全量再切片完全够用；真到了上万容器的规模，要考虑的是换聚合方案而不是把切片挪到别处。
/// 把夹紧后的页码与条数一并返回，调用方传 9999 时能看出服务端实际用了多少。
/// </remarks>
public sealed record ContainerListResult(
    IReadOnlyList<ContainerSummary> Items,
    int Total,
    int PageIndex,
    int PageSize);

/// <summary>
/// 容器详情（inspect 的<b>精选</b>投影）。
/// </summary>
/// <param name="Id">容器 ID。</param>
/// <param name="Name">容器名（无前导斜杠）。</param>
/// <param name="Created">创建时间（UTC）。</param>
/// <param name="Image">镜像名。</param>
/// <param name="State">运行状态。</param>
/// <param name="Config">启动配置。</param>
/// <param name="HostConfig">宿主侧配置。</param>
/// <param name="Mounts">挂载。</param>
/// <param name="Networks">网络接入信息。</param>
/// <remarks>
/// <b>为什么是投影而不是原样返回 SDK 的 inspect 对象</b>：那个对象有几十个嵌套层级，
/// 但运维真正会看的就是下面这些。投影的另一层价值是<b>写清楚哪些字段会给出</b>——
/// 直接透传 SDK 对象时，"响应里有什么"取决于 SDK 版本，前端无法依赖。
/// </remarks>
public sealed record ContainerDetail(
    string Id,
    string Name,
    DateTimeOffset Created,
    string Image,
    ContainerState State,
    ContainerConfigDetail Config,
    ContainerHostConfigDetail HostConfig,
    IReadOnlyList<ContainerMountDetail> Mounts,
    IReadOnlyList<ContainerNetworkDetail> Networks,
    IReadOnlyList<ContainerPort> Ports);

/// <summary>
/// 容器的运行状态。
/// </summary>
/// <param name="Status">状态文本（<c>running</c> / <c>exited</c> …）。</param>
/// <param name="Running">是否正在运行。</param>
/// <param name="Paused">是否已暂停。</param>
/// <param name="Restarting">是否正在重启。</param>
/// <param name="StartedAt">本次启动时间；未启动过为 <see langword="null"/>。</param>
/// <param name="FinishedAt">本次结束时间；未结束为 <see langword="null"/>。</param>
/// <param name="ExitCode">退出码；仍在运行或从未运行过为 <see langword="null"/>。</param>
/// <param name="OomKilled">是否因内存不足被杀。</param>
/// <param name="RestartCount">守护进程累计的重启次数。</param>
/// <param name="HealthStatus">
/// 健康检查状态；<b>未配置健康检查是 <see cref="ContainerHealthStatus.None"/>，不是 <see langword="null"/></b>——
/// 「没配」与「查不到」是两件事，混成一个值就没法在界面上分开表达。
/// </param>
public sealed record ContainerState(
    string Status,
    bool Running,
    bool Paused,
    bool Restarting,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long? ExitCode,
    bool OomKilled,
    long RestartCount,
    ContainerHealthStatus HealthStatus);

/// <summary>
/// 容器启动配置（精选）。
/// </summary>
/// <param name="Image">镜像名。</param>
/// <param name="Cmd">启动命令。</param>
/// <param name="Entrypoint">入口点。</param>
/// <param name="Env">环境变量（<c>KEY=VALUE</c>）。</param>
/// <param name="Labels">标签。</param>
/// <param name="WorkingDir">工作目录。</param>
/// <param name="Hostname">主机名。</param>
/// <param name="User">运行用户。</param>
/// <param name="Tty">是否分配 TTY。</param>
/// <remarks>
/// ⚠️ <see cref="Env"/> 常含密码 / Token 之类的敏感值（很多人把连接串塞进环境变量）。
/// 因此本投影<b>只</b>在 inspect（单容器详情）里给出，列表与列表项都不带它；
/// 0.1.0 起接入逐资源授权后，非管理员将不再能拿到主机级详情。
/// </remarks>
public sealed record ContainerConfigDetail(
    string Image,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Env,
    IReadOnlyDictionary<string, string> Labels,
    string WorkingDir,
    string Hostname,
    string User,
    bool Tty);

/// <summary>
/// 容器宿主侧配置（精选）。
/// </summary>
/// <param name="RestartPolicy">重启策略名（<c>no</c> / <c>always</c> / <c>unless-stopped</c> / <c>on-failure</c>）。</param>
/// <param name="AutoRemove">退出后是否自动删除。</param>
/// <param name="NetworkMode">网络模式。</param>
/// <param name="Privileged">是否特权模式。</param>
/// <param name="MemoryLimitBytes">内存上限（字节）；未限制为 <see langword="null"/>。</param>
/// <param name="NanoCpus">CPU 配额（十亿分之一核）；未限制为 <see langword="null"/>。</param>
public sealed record ContainerHostConfigDetail(
    string RestartPolicy,
    bool AutoRemove,
    string NetworkMode,
    bool Privileged,
    long? MemoryLimitBytes,
    long? NanoCpus);

/// <summary>
/// 挂载。
/// </summary>
/// <param name="Type">类型（<c>bind</c> / <c>volume</c> / <c>tmpfs</c>）。</param>
/// <param name="Source">宿主侧来源。</param>
/// <param name="Destination">容器内路径。</param>
/// <param name="ReadWrite">是否可写。</param>
public sealed record ContainerMountDetail(string Type, string Source, string Destination, bool ReadWrite);

/// <summary>
/// 网络接入信息。
/// </summary>
/// <param name="Name">网络名。</param>
/// <param name="IpAddress">容器在该网络中的 IP。</param>
/// <param name="Gateway">网关。</param>
/// <param name="MacAddress">MAC 地址。</param>
public sealed record ContainerNetworkDetail(string Name, string IpAddress, string Gateway, string MacAddress);

/// <summary>
/// 容器日志。
/// </summary>
/// <param name="ContainerId">容器标识（调用方传入的形态）。</param>
/// <param name="Lines">日志行（<b>按时间顺序</b>，序号从 1 起）。</param>
/// <param name="Truncated">是否因行数上限而被截断。</param>
/// <remarks>
/// <para><b>为什么从「整段文本」改成行数组</b>（0.5.0）：实时流要按行推送，
/// 而首屏与实时流必须共用一套行号——否则「第 4231 行」在两边指的是不同的行。
/// 文本形态表达不了行号，也表达不了每行的时间戳（Docker 把时间戳作为<b>行首前缀</b>返回，
/// 不摘出来的话前端既排不了序也定位不了）。</para>
/// <para><b>为什么带 <c>Truncated</c></b>：请求的 <c>tail</c> 超过上限时接口会报错（不静默截断），
/// 但「守护进程本身只有这么多日志」也是一种截断——两种情况必须能区分，
/// 否则用户会以为「日志丢了」。</para>
/// </remarks>
public sealed record ContainerLogsResult(
    string ContainerId,
    IReadOnlyList<Abstractions.Realtime.ContainerLogLine> Lines,
    bool Truncated);

/// <summary>
/// 创建容器的结果。
/// </summary>
/// <param name="ContainerId">新容器的完整 ID（由守护进程生成）。</param>
/// <param name="Warnings">守护进程的提示（如「端口未映射」）；无提示时为空列表。</param>
/// <remarks>
/// <b>为什么回带 <c>Warnings</c></b>：Docker 会在创建成功的同时给出警告（例如请求里的宿主端口
/// 没有被实际绑定）。只返回 ID 会把这些提示丢掉，而它们往往正是「容器起来了但访问不通」的原因。
/// <para>这里<b>不</b>返回容器名：未指定名称时名字由守护进程随机生成，要拿到它就得再 inspect 一次；
/// 而创建已经成功，为了补一个展示字段再发一次请求（失败了还会把成功当成错误）并不划算。</para>
/// </remarks>
public sealed record ContainerCreatedResult(string ContainerId, IReadOnlyList<string> Warnings);

// ==================== 0.3.0 基础操作的结果 ====================

/// <summary>
/// 重命名结果。
/// </summary>
/// <param name="ContainerId">容器 ID。</param>
/// <param name="Name">新名称。</param>
/// <remarks>
/// 回带名字而不是只返回 200：重命名后列表要能立刻刷新到新名字，
/// 而拿到新名字最省事的做法是让服务端回显，不必再 inspect 一次。</remarks>
public sealed record ContainerRenamedResult(string ContainerId, string Name);

/// <summary>
/// 容器更新结果（回带<b>更新后</b>的实际值）。
/// </summary>
/// <param name="ContainerId">容器 ID。</param>
/// <param name="RestartPolicy">更新后的重启策略。</param>
/// <param name="MemoryLimitBytes">更新后的内存上限（字节）；无限制为 <see langword="null"/>。</param>
/// <param name="NanoCpus">更新后的 CPU 配额；无限制为 <see langword="null"/>。</param>
/// <remarks>
/// ⚠️ <b>回显是必要的而不是顺手加的</b>：Docker 的 update 接口对「没传的字段」的处理
/// 在不同 API 版本间并不一致（老版本会把未传的数值字段一并写回默认值），
/// 且对传 <c>0</c> 的字段<b>返回成功但不应用</b>。
/// 因此本层在更新前先 inspect 一次取当前值、把未修改的字段按当前值回填，
/// 再回带最终结果——前端拿到的永远是「改完之后真实生效」的值，而不是「我请求的值」。
/// 本层也拒绝传 0，而不是让它变成一个静默无效的请求。
/// </remarks>
public sealed record ContainerUpdatedResult(
    string ContainerId,
    string RestartPolicy,
    long? MemoryLimitBytes,
    long? NanoCpus);

/// <summary>
/// 容器重建结果（复制与追加挂载共用）。
/// </summary>
/// <param name="ContainerId">新容器的完整 ID。</param>
/// <param name="Name">新容器名。</param>
/// <param name="ReplacedContainerId">被让位（改名保留）的旧容器 ID；复制场景下为 <see langword="null"/>。</param>
/// <param name="Running">新容器当前是否在运行。</param>
/// <param name="Warnings">守护进程的提示。</param>
/// <param name="Recreated">是否发生了重建（追加挂载恒为 <see langword="true"/>）。</param>
/// <remarks>
/// <b>为什么回带旧容器 ID</b>：追加挂载之后旧容器仍以临时名存在——那是回滚的后手，
/// 也是给用户「配置不对还能捞回来」的机会。前端必须能指到它，
/// 否则界面上会出现一个来历不明的容器而没人敢删。
/// </remarks>
public sealed record ContainerRecreatedResult(
    string ContainerId,
    string Name,
    string? ReplacedContainerId,
    bool Running,
    IReadOnlyList<string> Warnings,
    bool Recreated);
