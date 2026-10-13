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
