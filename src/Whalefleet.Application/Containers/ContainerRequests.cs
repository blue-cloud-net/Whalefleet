namespace Whalefleet.Application.Containers;

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

    /// <summary>
    /// 排序字段；省略时保持守护进程返回的顺序（创建时间降序）。
    /// </summary>
    /// <remarks>
    /// 取值见 <see cref="ContainerSortField"/> 的常量。**未知值一律按创建时间处理**，
    /// 而不是报错——排序字段来自客户端，版本错配时不该让整个列表 400。
    /// </remarks>
    public string? SortBy { get; init; }

    /// <summary>排序方向；<c>asc</c> 为升序，其余（含省略）按降序。</summary>
    /// <remarks>不校验：无法识别的值按降序处理，与「省略」行为一致。</remarks>
    public string? SortDir { get; init; }
}

/// <summary>
/// 容器列表的排序字段取值。
/// </summary>
/// <remarks>
/// 与 <c>ImageSortField</c> 同构：常量而非枚举，因为请求字段是字符串，
/// 「未知值回落」这条策略需要在 <c>switch</c> 的默认分支里显式可见。
/// </remarks>
public static class ContainerSortField
{
    /// <summary>容器名。</summary>
    public const string Name = "name";

    /// <summary>镜像名。</summary>
    public const string Image = "image";

    /// <summary>状态（运行中 / 已停止 / …）。</summary>
    public const string State = "state";

    /// <summary>创建时间。</summary>
    public const string Created = "created";

    /// <summary>默认字段：等价于守护进程自己给的创建时间降序。</summary>
    public const string Default = Created;
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

/// <summary>
/// 创建容器请求（<b>精选字段</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么不是 <c>CreateContainerParameters</c> 的全部字段</b>：那个模型有上百个字段
/// （<c>Ulimits</c> / <c>DeviceRequests</c> / <c>BlkioWeightDevice</c> / <c>Sysctls</c> …），
/// 绝大多数在运维面板里永远不会出现。全量透传等于把 SDK 的字段集当对外契约——SDK 升级就要改前端，
/// 调用方也无法从文档里看出「到底哪些字段真的会被用上」。这里只暴露日常创建容器真正会设的那一组，
/// 需要 <c>--cap-add</c> / <c>--device</c> 这类高级选项时再逐个补齐（每个都要配语义说明与校验）。</para>
/// <para><b>字段一律可空</b>：缺字段是常态，校验统一在 <c>ContainerService</c> 里做并给出可读消息。</para>
/// </remarks>
public sealed record CreateContainerRequest
{
    /// <summary>镜像名（如 <c>nginx:latest</c>）；必填。</summary>
    public string? Image { get; init; }

    /// <summary>容器名；省略时由 Docker 随机生成。</summary>
    public string? Name { get; init; }

    /// <summary>启动命令（覆盖镜像的 <c>CMD</c>）。</summary>
    public IReadOnlyList<string>? Cmd { get; init; }

    /// <summary>入口点（覆盖镜像的 <c>ENTRYPOINT</c>）。</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }

    /// <summary>环境变量（<c>KEY=VALUE</c>）。</summary>
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>标签。</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>端口映射。</summary>
    public IReadOnlyList<ContainerPortMapping>? Ports { get; init; }

    /// <summary>挂载（bind / 具名卷）。</summary>
    public IReadOnlyList<ContainerVolumeMapping>? Volumes { get; init; }

    /// <summary>重启策略（<c>no</c> / <c>always</c> / <c>on-failure</c> / <c>unless-stopped</c>）；省略等同 <c>no</c>。</summary>
    public string? RestartPolicy { get; init; }

    /// <summary>退出后是否自动删除容器。</summary>
    public bool? AutoRemove { get; init; }

    /// <summary>网络名或网络模式（如 <c>bridge</c> / <c>host</c> / <c>none</c> / 自定义网络名）。</summary>
    public string? Network { get; init; }

    /// <summary>容器主机名。</summary>
    public string? Hostname { get; init; }

    /// <summary>工作目录。</summary>
    public string? WorkingDir { get; init; }

    /// <summary>是否分配 TTY。</summary>
    public bool? Tty { get; init; }

    /// <summary>是否保持标准输入打开（<c>docker run -i</c>）。</summary>
    public bool? StdinOpen { get; init; }

    // ==================== 0.3.0 高级创建参数 ====================
    // 界面按「常用 / 高级」两级折叠：上面这一组是高频，下方这一组平时不碰。
    // 拆成两组而不是两套端点，是因为 Docker 的创建接口只有一个，
    // 而「高级参数」在服务端与普通参数没有任何处理差异——差别只在界面上。

    /// <summary>运行用户（对应 <c>docker run --user</c>）；省略时用镜像里 <c>USER</c> 声明的用户。</summary>
    public string? User { get; init; }

    /// <summary>新增的 Linux capabilities（对应 <c>--cap-add</c>）。</summary>
    public IReadOnlyList<string>? CapAdd { get; init; }

    /// <summary>移除的 Linux capabilities（对应 <c>--cap-drop</c>）。</summary>
    public IReadOnlyList<string>? CapDrop { get; init; }

    /// <summary>健康检查定义；省略时用镜像里的 <c>HEALTHCHECK</c>。</summary>
    /// <remarks>传 <c>["NONE"]</c> 表示<b>覆盖</b>镜像的健康检查并关闭它——这是 Docker 的既定语义，
    /// 与「不传」截然不同，因此单独用一条注释标出来。</remarks>
    public ContainerHealthCheck? HealthCheck { get; init; }

    /// <summary>设备映射（对应 <c>--device</c>）。</summary>
    public IReadOnlyList<ContainerDeviceMapping>? Devices { get; init; }

    /// <summary>可写内核参数（对应 <c>--sysctl</c>）；键值成对。</summary>
    public IReadOnlyDictionary<string, string>? Sysctls { get; init; }

    /// <summary>资源限制（对应 <c>--ulimit</c>）。</summary>
    public IReadOnlyList<ContainerUlimit>? Ulimits { get; init; }

    /// <summary>日志驱动配置。</summary>
    public ContainerLogDriver? LogDriver { get; init; }

    /// <summary>内存上限（字节）；省略为不限制。</summary>
    public long? MemoryLimitBytes { get; init; }

    /// <summary>CPU 配额（十亿分之一核）；省略为不限制。</summary>
    public long? NanoCpus { get; init; }
}

/// <summary>
/// 端口映射项。
/// </summary>
/// <remarks>
/// <b>为什么不用 <c>"8080:80/tcp"</c> 这种字符串</b>：字符串形式省几个字符，代价是每个调用方
/// 各自解析与校验（“宿主:容器”与“容器”两种写法还得靠正则区分）。结构化字段让校验只有一处。
/// </remarks>
public sealed record ContainerPortMapping
{
    /// <summary>容器内端口（1–65535）；必填。</summary>
    public int ContainerPort { get; init; }

    /// <summary>宿主端口（1–65535）；省略表示<b>只暴露不绑定</b>（随机映射）。</summary>
    public int? HostPort { get; init; }

    /// <summary>协议（<c>tcp</c> / <c>udp</c>）；省略为 <c>tcp</c>。</summary>
    public string? Type { get; init; }

    /// <summary>绑定的宿主地址；省略表示所有网卡。</summary>
    public string? HostIp { get; init; }
}

/// <summary>
/// 挂载项。
/// </summary>
/// <remarks>
/// <c>source</c> 既可以是宿主路径（bind），也可以是卷名（named volume）——两者的区别由 Docker 根据
/// 该路径是否存在且像路径来判定，无需在请求里多一个 <c>type</c> 字段去重复它的规则。
/// </remarks>
public sealed record ContainerVolumeMapping
{
    /// <summary>宿主路径或卷名；必填。</summary>
    public string? Source { get; init; }

    /// <summary>容器内路径；必填。</summary>
    public string? Destination { get; init; }

    /// <summary>是否只读挂载；省略为可写。</summary>
    public bool? ReadOnly { get; init; }
}

/// <summary>
/// 启动容器请求。
/// </summary>
public sealed record StartContainerRequest
{
    /// <summary>容器标识（完整 ID / 短 ID / 名称）。</summary>
    public string? ContainerId { get; init; }
}

/// <summary>
/// 停止容器请求。
/// </summary>
public sealed record StopContainerRequest
{
    /// <summary>容器标识。</summary>
    public string? ContainerId { get; init; }

    /// <summary>收到 SIGTERM 后等待多久才 SIGKILL（秒）；省略为 10（与 Docker 默认一致）。</summary>
    public int? WaitSeconds { get; init; }
}

/// <summary>
/// 重启容器请求。
/// </summary>
public sealed record RestartContainerRequest
{
    /// <summary>容器标识。</summary>
    public string? ContainerId { get; init; }
}

/// <summary>
/// 删除容器请求。
/// </summary>
public sealed record RemoveContainerRequest
{
    /// <summary>容器标识。</summary>
    public string? ContainerId { get; init; }

    /// <summary>是否强制删除运行中的容器；省略为 <see langword="false"/>（此时守护进程会以 409 拒绝）。</summary>
    public bool? Force { get; init; }
}

/// <summary>
/// 批量删除容器请求。
/// </summary>
public sealed record BatchRemoveContainersRequest
{
    /// <summary>要删除的容器标识列表；必填且不能为空。</summary>
    public IReadOnlyList<string>? ContainerIds { get; init; }

    /// <summary>是否强制删除运行中的容器；省略为 <see langword="false"/>（此时这些容器会以 409 失败）。</summary>
    public bool? Force { get; init; }
}

// ==================== 0.3.0 高级创建参数与基础操作 ====================

/// <summary>
/// 容器健康检查定义。
/// </summary>
/// <remarks>
/// <para><b>为什么 <see cref="Test"/> 是数组而不是一个字符串</b>：Docker 的 healthcheck 有三种形态，
/// 而它们在 API 上是同一个字段的不同写法——
/// <c>["CMD-SHELL", "curl -f http://localhost/ || exit 1"]</c>（交给守护进程跑 shell）、
/// <c>["CMD", "curl", "-f", "..."]</c>（不经 shell，参数不会被二次拆分）、
/// 以及 <c>["NONE"]</c>（关闭检查）。把它们压成一个字符串就得靠分隔符再解析回来，
/// 而命令里本身带引号与空格——解析规则与 Docker 不一致时，
/// 症状是「配置看着对但没生效」。这里原样保留数组形状，让调用方明确选择哪一种。</para>
/// <para><b>时间单位对外一律用秒</b>：Docker 的 API 用纳秒，
/// 那是 SDK 与守护进程之间的实现细节；对外用秒可读，换算在服务端做。</para>
/// </remarks>
public sealed record ContainerHealthCheck
{
    /// <summary>检查命令；<c>["NONE"]</c> 表示关闭检查。必填。</summary>
    public IReadOnlyList<string>? Test { get; init; }

    /// <summary>两次检查之间的间隔（秒）；省略时由镜像的 <c>HEALTHCHECK</c> 或守护进程默认值决定。</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>单次检查的超时（秒）。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>容器启动后的「宽限期」（秒）：期间检查失败不计入 <see cref="Retries"/>。</summary>
    public int? StartPeriodSeconds { get; init; }

    /// <summary>连续失败多少次判定为 <c>unhealthy</c>；省略为 3。</summary>
    public int? Retries { get; init; }
}

/// <summary>
/// 设备映射（对应 <c>docker run --device</c>）。
/// </summary>
/// <remarks>
/// ⚠️ <b>这不是「普通用户」该用的功能</b>：把宿主设备映射进容器等价于把宿主上那个设备的
/// 控制权交给容器（把 <c>/dev/sda</c> 映射进去基本等于交出整块磁盘）。
/// 因此本项目既不提供 <c>--privileged</c> 的快捷方式，也不把它当普通创建参数推荐。
/// </remarks>
public sealed record ContainerDeviceMapping
{
    /// <summary>宿主设备路径（如 <c>/dev/snd</c>）；必填。</summary>
    public string? HostPath { get; init; }

    /// <summary>容器内路径；省略时与 <see cref="HostPath"/> 相同（与 <c>docker run</c> 一致）。</summary>
    public string? ContainerPath { get; init; }

    /// <summary>cgroup 设备权限：<c>r</c> 读 / <c>w</c> 写 / <c>m</c> mknod；省略为 <c>rwm</c>。</summary>
    public string? Permissions { get; init; }
}

/// <summary>
/// 资源限制项（对应 <c>docker run --ulimit</c>）。
/// </summary>
public sealed record ContainerUlimit
{
    /// <summary>限制名（如 <c>nofile</c> / <c>nproc</c> / <c>fsize</c>）；必填。</summary>
    public string? Name { get; init; }

    /// <summary>软限制；省略时与 <see cref="Hard"/> 相同。</summary>
    public long? Soft { get; init; }

    /// <summary>硬限制；省略时与 <see cref="Soft"/> 相同。</summary>
    public long? Hard { get; init; }
}

/// <summary>
/// 日志驱动配置。
/// </summary>
/// <remarks>
/// <b>为什么不给「全部驱动」的清单</b>：驱动名是守护进程插件注册表里的字符串，
/// 不同守护进程可用的驱动不同（<c>journald</c> 需要 systemd、<c>syslog</c> 需要 rsyslog）。
/// 传了本机没有的驱动时，守护进程会在<b>启动容器时</b>才失败——那时容器已经建出来了，
/// 症状是「创建成功、一启动就退出」。本层无法枚举可用驱动（那要读宿主配置），
/// 因此只做非空校验，把「驱动是否存在」如实交给守护进程判定。
/// </remarks>
public sealed record ContainerLogDriver
{
    /// <summary>驱动名（<c>json-file</c> / <c>local</c> / <c>journald</c> / <c>syslog</c> / <c>none</c>）；必填。</summary>
    public string? Type { get; init; }

    /// <summary>驱动选项（如 <c>max-size</c> / <c>max-file</c>）；键值成对。</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>
/// 容器重命名请求。
/// </summary>
public sealed record RenameContainerRequest
{
    /// <summary>容器标识（完整 ID / 短 ID / 名称）。</summary>
    public string? ContainerId { get; init; }

    /// <summary>新名称；必填。</summary>
    public string? Name { get; init; }
}

/// <summary>
/// 容器热更新请求（**只开放守护进程真支持热改的字段**）。
/// </summary>
/// <remarks>
/// <para><b>为什么字段这么少</b>：Docker 的 <c>POST /containers/{id}/update</c> 只接受
/// 资源类字段与重启策略。镜像、端口、环境变量、挂载、网络<b>都不能</b>热改——
/// 传过去要么被守护进程忽略，要么回一句看不懂的错。因此这里刻意只暴露三组字段，
/// 而不在界面上摆一堆「保存后不生效」的输入框。</para>
/// <para><b>每组字段都可为 <see langword="null"/></b>：省略 = 保持当前值。
/// 但 <b>0 不是「取消限制」</b>——实测 <c>docker update --memory=0</c> 会<b>返回成功而容器上的限制没变</b>
/// （守护进程对 update 的处理是「字段非 0 才应用」，于是 0 与「不传」同义）。
/// 因此本端点把 0 视为非法并报错：要真的取消限制只能重建容器。</para>
/// </remarks>
public sealed record UpdateContainerRequest
{
    /// <summary>容器标识。</summary>
    public string? ContainerId { get; init; }

    /// <summary>新的重启策略；省略表示不改。</summary>
    public string? RestartPolicy { get; init; }

    /// <summary>内存上限（字节）；省略表示不改，<b>0 非法</b>（见类型注释）。</summary>
    public long? MemoryLimitBytes { get; init; }

    /// <summary>CPU 配额（十亿分之一核）；省略表示不改，<b>0 非法</b>（见类型注释）。</summary>
    public long? NanoCpus { get; init; }
}

/// <summary>
/// 容器复制（duplicate）请求。
/// </summary>
/// <remarks>
/// <b>语义是「新建一个配置相同的兄弟容器」</b>，不是原地克隆：
/// 目标名必须与原容器不同（同一守护进程上不能有两个同名容器），
/// 因此默认在原名后加 <c>-copy</c>。原容器<b>不受影响</b>（不停止、不改名、不删除）。
/// </remarks>
public sealed record DuplicateContainerRequest
{
    /// <summary>源容器标识。</summary>
    public string? ContainerId { get; init; }

    /// <summary>新容器名；省略时在原名后加 <c>-copy</c>，仍冲突则继续追加序号。</summary>
    public string? Name { get; init; }

    /// <summary>创建后是否立即启动；省略为 <see langword="false"/>（与 <c>create</c> 端点一致）。</summary>
    public bool? Start { get; init; }
}

/// <summary>
/// 为已存在容器追加卷请求。
/// </summary>
/// <remarks>
/// <para>⚠️ <b>这个操作会重建容器</b>，因此容器 ID 会变。Docker 没有「给运行中的容器加挂载」的接口，
/// 唯一途径是用新配置重建。实现上先把原容器<b>改名让位</b>（不是删除），再用原名创建，
/// 失败时改回原名并恢复原运行状态——这样回滚不依赖「删掉的东西能不能救回来」。</para>
/// <para><b>追加而不是覆盖</b>：请求里给的是「要新增的挂载」，原有挂载全部保留；
/// 移除某个挂载不在本端点的能力范围内。</para>
/// </remarks>
public sealed record AddContainerVolumesRequest
{
    /// <summary>容器标识。</summary>
    public string? ContainerId { get; init; }

    /// <summary>要追加的挂载；必填且不能为空。</summary>
    public IReadOnlyList<ContainerVolumeMapping>? Volumes { get; init; }

    /// <summary>
    /// 重建成功后是否立即启动；<see langword="null"/> 表示<b>沿用原容器的运行状态</b>。
    /// </summary>
    /// <remarks>默认「保持原状」而不是「总是启动」：
    /// 对一个本来就没运行的容器擅自启动它，是用户没要求的副作用。</remarks>
    public bool? Start { get; init; }
}
