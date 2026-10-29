import { http, request } from '@/api'
import type {
  ContainerCreatedResult,
  ContainerDetail,
  ContainerHealthResult,
  ContainerListResult,
  ContainerLogsResult,
  ContainerRecreatedResult,
  ContainerRenamedResult,
  ContainerTerminalToken,
  ContainerUpdatedResult
} from '@/types/api'

/**
 * 容器端点（对应后端 `/api/v1/hosts/{hostId}/containers/*`，0.0.9 已实现）。
 *
 * ## 路径约定（见 docs/api-spec.md）
 *
 * 全部是 **主机作用域**（路径含 `hostId`）与 **RPC 风格**（一律 POST）：
 * - 查询 `list` / `inspect` / `logs`
 * - 变更 `create` / `start` / `stop` / `restart` / `remove`
 *
 * `hostId` 要**编码**后拼进路径：主机名虽是 `[A-Za-z0-9._-]`（设计上可直接进 URL），
 * 但 encodeURIComponent 成本为零，而漏掉它一旦将来放开主机名字符集就是线上问题。
 *
 * ## 为什么全部返回 Promise<结果> 而不是 void
 * `start` / `stop` / `restart` / `remove` 后端不返回 `data`，但仍返回统一响应——
 * 拦截器已经解包，这里 `Promise<void>` 即可。保留 void 而不是 `Promise<unknown>`，
 * 是为了让调用方在类型层面就知道「这里没有东西可拿」。
 */

/** 主机作用域的容器端点基址。 */
function base(hostId: string): string {
  return `/api/v1/hosts/${encodeURIComponent(hostId)}/containers`
}

/** 列表查询参数（全部可选，对应后端的可空请求字段）。 */
export interface ListContainersParams {
  /** 是否含已停止的容器；省略时只列运行中的（等价 `docker ps`） */
  all?: boolean
  /**
   * 状态筛选；省略时按后端 `state` 的默认值（全部）处理。
   *
   * ⚠️ **优先于 `all`**：后端收到 `state` 时不再看 `all`。
   * 保留 `all` 只是为了兼容旧客户端。
   */
  state?: ContainerStateFilter
  /**
   * 关键字搜索：按容器名 / 镜像名 / 容器 ID 模糊匹配（不区分大小写）。
   *
   * 在**服务端**过滤：分页也在服务端做，前端过滤只能筛到当前页。
   */
  search?: string
  /** 页码，从 0 开始 */
  pageIndex?: number
  /** 每页条数；后端上限 100，超出会被**夹紧并回显实际值** */
  pageSize?: number
  /** 排序字段；省略时保持后端返回顺序（创建时间降序）。未知值后端按 `created` 处理 */
  sortBy?: ContainerSortField
  /** 排序方向；`asc` 为升序，其余（含省略）按降序 */
  sortDir?: SortDirection
}

/** 容器列表可排序的字段，与后端 `ContainerSortField` 常量一一对应。 */
export type ContainerSortField = 'name' | 'image' | 'state' | 'created'

/**
 * 容器列表的状态筛选，与后端 `ContainerStateFilter` 常量一一对应。
 *
 * `stopped` 的口径是「**未运行**」：已退出 / 已死亡 / 从未启动都算，
 * 而已暂停与重启中仍算「还活着」（与 `docker ps` 一致）。
 */
export type ContainerStateFilter = 'all' | 'running' | 'stopped'

/** 排序方向。 */
export type SortDirection = 'asc' | 'desc'

/**
 * 列出容器。
 *
 * 分页在**后端内存**完成（Docker API 无分页参数），因此返回的 `pageIndex` / `pageSize`
 * 是服务端实际使用的值——页面应以它们为准，而不是以请求里写的为准。
 */
export function listContainers(
  hostId: string,
  params: ListContainersParams = {}
): Promise<ContainerListResult> {
  return request<ContainerListResult>({
    url: `${base(hostId)}/list`,
    method: 'post',
    data: params
  })
}

/**
 * 查询容器详情。
 *
 * @param imageId 容器标识：完整 ID / 短 ID / 名称都接受。
 */
export function inspectContainer(hostId: string, imageId: string): Promise<ContainerDetail> {
  return request<ContainerDetail>({
    url: `${base(hostId)}/inspect`,
    method: 'post',
    data: { containerId: imageId }
  })
}

/**
 * 批量查询容器健康状态与退出原因（0.6.0 列表健康列的数据源）。
 *
 * ⚠️ **查不到的容器直接缺席**（多半是被删掉了），前端对缺失项显示「—」。
 * 上限 100 项（与列表分页上限一致）。
 */
export function getContainerHealth(
  hostId: string,
  containerIds: string[]
): Promise<ContainerHealthResult> {
  return request<ContainerHealthResult>({
    url: `${base(hostId)}/health`,
    method: 'post',
    data: { containerIds }
  })
}

/** 容器日志的查询参数。 */
export interface ContainerLogsParams {
  /** 只取最后 N 行；省略表示全部。**0.5.0 起上限 5000**（越界返回 1000）。 */
  tail?: number
  /** 只取该 Unix 秒（含）之后的日志 */
  since?: number
  /** 只取该 Unix 秒（不含）之前的日志；用于「向上加载更早的日志」 */
  until?: number
  /** 是否包含 stderr；省略时包含 */
  includeStderr?: boolean
}

/**
 * 申请一个打开交互式终端的短时令牌（0.8.0）。
 *
 * ## 为什么是「先要令牌，再建 WebSocket」
 * 浏览器建立 WebSocket 时**不能自定义请求头**，令牌只能出现在 URL 里，
 * 而 URL 会进访问日志、反代日志与浏览器历史——这正是不能直接放 access token 的原因。
 * 服务端签发的是一张 5 分钟、只对一条会话有效的票（容器与命令都绑在票里）。
 *
 * ## 为什么错误处理放在这一步
 * WebSocket 握手失败时前端只拿到一个 `close` 事件（通常还是 1006），
 * **看不到响应体、也看不到状态码**。因此「容器不存在 `1401` / 未运行 `1404` /
 * 没权限 `1502`」这些诊断信息必须由本端点给出——它是唯一能把原因讲清楚的地方。
 *
 * @param hostId 主机标识。
 * @param params 容器与可选的 shell / 命令 / 尺寸。
 * @returns 令牌与过期时间。
 */
export function createTerminalToken(
  hostId: string,
  params: ContainerTerminalParams
): Promise<ContainerTerminalToken> {
  return request<ContainerTerminalToken>({
    url: `${base(hostId)}/terminal-token`,
    method: 'post',
    data: params
  })
}

/**
 * 申请终端令牌的参数。
 */
export interface ContainerTerminalParams {
  /** 容器 ID / 名称 */
  containerId: string
  /** 要启动的 shell 绝对路径；省略时由服务端取默认值（`/bin/sh`） */
  shell?: string
  /** 非交互模式下要执行的一条命令；省略表示开交互式 shell */
  command?: string
  /** 初始列数 */
  columns?: number
  /** 初始行数 */
  rows?: number
}

/**
 * 读取容器日志（一次性返回整段文本）。
 *
 * **不支持实时跟随**：后端刻意没暴露 `follow`（一次性 HTTP 响应跟着日志流只会把连接挂住）。
 * 实时流属 0.4.0 的 SignalR 推送。
 */
export function getContainerLogs(
  hostId: string,
  containerId: string,
  params: ContainerLogsParams = {}
): Promise<ContainerLogsResult> {
  return request<ContainerLogsResult>({
    url: `${base(hostId)}/logs`,
    method: 'post',
    data: { containerId, ...params }
  })
}

/**
 * 下载容器日志（0.5.0 新增）。
 *
 * ## 为什么不用 `getContainerLogs` 的结果拼成文件
 * 一次性接口受 **tail 上限（5000 行）**约束，导出的文件会缺头；
 * 而用户点「下载」要的通常就是完整日志。
 * 本端点在服务端直接跟着守护进程的日志流边读边写，**不受 tail 上限约束**。
 *
 * ## 返回的是原始文本而不是 JSON
 * 响应体是 `text/plain`，逐行输出。`responseType: 'blob'` 让浏览器把整个响应
 * 当二进制收下，再由前端 `URL.createObjectURL` 触发下载。
 *
 * ⚠️ **错误响应仍是 JSON**（`ApiResult`）：后端在开始写正文之前才知道
 * 主机不可达 / 容器不存在，这时候响应头还没发出去，可以正常返回 JSON 错误体。
 * 前端因此要**先看 content-type** 再决定按文本还是按 JSON 解析。
 *
 * @param hostId 主机标识。
 * @param containerId 容器 ID。
 * @param params 可选参数（`since` / `includeStderr`）。
 * @returns 日志文件名（后端生成）。
 */
export async function downloadContainerLogs(
  hostId: string,
  containerId: string,
  params: Omit<ContainerLogsParams, 'tail' | 'until'> = {}
): Promise<{ blob: Blob; fileName: string }> {
  const response = await http.post(`${base(hostId)}/logs/download`, { containerId, ...params }, {
    responseType: 'blob'
  })

  return {
    blob: response.data as Blob,
    fileName: fileNameFromDisposition(response.headers['content-disposition'])
  }
}

/**
 * 从 `Content-Disposition` 里取文件名。
 *
 * 后端同时给出 ASCII 回退名（`filename=`）与 UTF-8 名（`filename*=`），
 * 优先取后者。
 *
 * @param disposition 响应头原值。
 * @returns 文件名；取不到时返回默认名。
 */
function fileNameFromDisposition(disposition: string | undefined): string {
  if (!disposition) {
    return 'container.log'
  }

  const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(disposition)
  if (star?.[1]) {
    return decodeURIComponent(star[1].trim())
  }

  const plain = /filename="?([^";]+)"?/i.exec(disposition)
  return plain?.[1]?.trim() || 'container.log'
}

/** 创建容器的请求体。字段与后端 `CreateContainerRequest` 逐字对应。 */
export interface CreateContainerPayload {
  /** 镜像名；必填 */
  image: string
  /** 容器名；省略则由 Docker 随机命名 */
  name?: string
  cmd?: string[]
  entrypoint?: string[]
  env?: string[]
  labels?: Record<string, string>
  ports?: Array<{
    /** 容器内端口 1–65535；必填 */
    containerPort: number
    /** 宿主端口；省略表示只暴露不绑定（随机映射） */
    hostPort?: number
    /** `tcp` / `udp`；省略为 tcp */
    type?: string
    /** 宿主地址；省略表示所有网卡 */
    hostIp?: string
  }>
  volumes?: Array<{
    /** 宿主路径或卷名；必填 */
    source: string
    /** 容器内路径；必填 */
    destination: string
    readOnly?: boolean
  }>
  /** `no` / `always` / `on-failure` / `unless-stopped`；省略等同 no */
  restartPolicy?: string
  autoRemove?: boolean
  /** 网络名或模式 */
  network?: string
  hostname?: string
  workingDir?: string
  tty?: boolean
  stdinOpen?: boolean

  // ===== 0.3.0：高级创建参数 =====
  // 界面按「常用 / 高级」两级折叠。服务端对这组字段没有任何特殊处理
  // （Docker 的创建接口只有一个），差别只在 UI 上。

  /** 运行用户（`docker run --user`）；省略时用镜像里的 `USER` */
  user?: string
  capAdd?: string[]
  capDrop?: string[]
  /**
   * 健康检查定义。
   *
   * `test` 是**数组**：Docker 有 `CMD-SHELL` / `CMD` / `NONE` 三种形态，
   * 而它们在 API 上是同一字段的不同写法。压成字符串就得再解析回来，
   * 而命令里本身带引号与空格——解析规则一旦与 Docker 不一致，
   * 症状就是「配置看着对但没生效」。
   *
   * `['NONE']` 表示**覆盖镜像的健康检查并关闭它**（与「不传」截然不同）。
   */
  healthCheck?: {
    test: string[]
    /** 两次检查的间隔（秒）；后端下限 1 */
    intervalSeconds?: number
    /** 单次检查超时（秒） */
    timeoutSeconds?: number
    /** 启动宽限期（秒），期间失败不计入 retries */
    startPeriodSeconds?: number
    /** 连续失败多少次判定 unhealthy；后端下限 1 */
    retries?: number
  }
  /**
   * 设备映射（`--device`）。
   *
   * ⚠️ 把宿主设备映射进容器等于把那个设备的控制权交给容器，
   * 因此 UI 不应把它当普通创建参数推荐。
   */
  devices?: Array<{
    /** 宿主设备路径；必填 */
    hostPath: string
    /** 容器内路径；省略则与宿主路径相同 */
    containerPath?: string
    /** `r`(读) / `w`(写) / `m`(mknod) 的组合；省略为 `rwm` */
    permissions?: string
  }>
  sysctls?: Record<string, string>
  ulimits?: Array<{
    /** 限制名，如 `nofile`；必填 */
    name: string
    /** 软限制；省略时与 hard 相同 */
    soft?: number
    /** 硬限制；省略时与 soft 相同 */
    hard?: number
  }>
  /**
   * 日志驱动。
   *
   * ⚠️ **驱动的存在性无法在服务端校验**（要读宿主守护进程的插件注册表）。
   * 传了本机没有的驱动时，容器能创建成功、**一启动就退出**——
   * 因此 UI 应给出「该驱动必须在目标主机上已安装」的提示。
   */
  logDriver?: {
    /** `json-file` / `local` / `journald` / `syslog` / `none`；必填 */
    type: string
    options?: Record<string, string>
  }
  /** 内存上限（字节）；**下限 6 MiB**（守护进程硬性下限）。省略或 0 = 不限制 */
  memoryLimitBytes?: number
  /** CPU 配额（十亿分之一核，`1e9` = 1 核）；上限 1024 核。省略或 0 = 不限制 */
  nanoCpus?: number
}

/**
 * 创建容器（**只创建，不自动启动**）。
 *
 * 返回的 `warnings` 是守护进程的提示（如「端口未映射」），**不是错误**——
 * 创建已经成功，调用方应当展示它们而不是当成失败。
 */
export function createContainer(
  hostId: string,
  payload: CreateContainerPayload
): Promise<ContainerCreatedResult> {
  return request<ContainerCreatedResult>({
    url: `${base(hostId)}/create`,
    method: 'post',
    data: payload
  })
}

/** 启动容器（幂等：已在运行也返回成功）。 */
export function startContainer(hostId: string, containerId: string): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/start`,
    method: 'post',
    data: { containerId }
  })
}

/**
 * 停止容器（幂等：已停止也返回成功）。
 *
 * @param waitSeconds SIGTERM 后等多久才 SIGKILL；省略则由守护进程用默认值（10 秒）
 */
export function stopContainer(
  hostId: string,
  containerId: string,
  waitSeconds?: number
): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/stop`,
    method: 'post',
    data: { containerId, waitSeconds }
  })
}

/** 重启容器（幂等）。 */
export function restartContainer(hostId: string, containerId: string): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/restart`,
    method: 'post',
    data: { containerId }
  })
}

/**
 * 删除容器（**不连带删除数据卷**）。
 *
 * 不带 `force` 删运行中的容器会得到 `1404`/409（前端据此提示先停止），
 * 因此「删不掉」是有意义的业务结果，调用方要把它渲染成可操作的提示而不是通用报错。
 */
export function removeContainer(
  hostId: string,
  containerId: string,
  force = false
): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/remove`,
    method: 'post',
    data: { containerId, force }
  })
}

/**
 * 终止容器（等价于 `docker kill`：直接把信号送到容器主进程，**不等优雅退出**）。
 *
 * 与 `stopContainer` 的分工：`stop` 先发 SIGTERM 并等进程自己退出，
 * `kill` 不给机会——正常停机用 `stop`，进程卡死时才用 `kill`。
 *
 * @param signal 要发送的信号（如 `SIGKILL` / `SIGHUP` / `9`）；省略则由守护进程取默认值。
 *   ⚠️ **非终止信号只是「发个信号」**：容器可能继续运行，因此界面必须写明这一点。
 */
export function killContainer(
  hostId: string,
  containerId: string,
  signal?: string
): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/kill`,
    method: 'post',
    data: { containerId, signal }
  })
}

/**
 * 暂停容器（等价于 `docker pause`）。
 *
 * ⚠️ **暂停 ≠ 停止**：容器进程仍在，只是不再占用 CPU；
 * 它仍占着映射的端口与挂载的卷。
 *
 * 幂等：已经暂停时再调一次也返回成功。
 */
export function pauseContainer(hostId: string, containerId: string): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/pause`,
    method: 'post',
    data: { containerId }
  })
}

/**
 * 恢复容器（等价于 `docker unpause`）。
 *
 * ⚠️ **与「启动」不是一回事**：本函数只解除**暂停**，容器随即继续跑；
 * `startContainer` 是把**已停止**的容器跑起来。
 *
 * 幂等：未处于暂停状态时再调一次也返回成功。
 */
export function resumeContainer(hostId: string, containerId: string): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/resume`,
    method: 'post',
    data: { containerId }
  })
}

/**
 * 重建容器（⚠️ **会重建容器**，容器 ID 一定变）。
 *
 * Docker 没有「原地改配置」的接口——镜像、命令、端口、挂载、网络一旦要改就只能重建。
 * 因此界面**必须**把这一点写在二次确认里。
 *
 * @param config 新的配置；**省略 = 按原容器的现有配置原样重建**。
 *   给了则**整份替换**（不是补丁）：没写的字段就是「不要」。
 *   ⚠️ `config.name` 会被后端忽略——重建沿用原名，改名走 `renameContainer`。
 * @param start 重建后是否启动；**省略则沿用原容器的运行状态**。
 */
export function recreateContainer(
  hostId: string,
  containerId: string,
  config?: CreateContainerPayload,
  start?: boolean
): Promise<ContainerRecreatedResult> {
  return request<ContainerRecreatedResult>({
    url: `${base(hostId)}/recreate`,
    method: 'post',
    data: { containerId, config, start }
  })
}

/* ==================================================================
 * 基础操作（0.3.0）
 * ================================================================== */

/**
 * 重命名容器。
 *
 * 名称规则与创建相同（字母/数字开头，其后含 `_ . -`），非法 → `1000`/400；
 * 名称已被占用 → `1404`/409（界面应提示「换个名字」，不是通用报错）。
 */
export function renameContainer(
  hostId: string,
  containerId: string,
  name: string
): Promise<ContainerRenamedResult> {
  return request<ContainerRenamedResult>({
    url: `${base(hostId)}/rename`,
    method: 'post',
    data: { containerId, name }
  })
}

/** 热更新的请求体。三个字段都省略 → `1000`（那等于什么都不改）。 */
export interface UpdateContainerPayload {
  /** `no` / `always` / `on-failure` / `unless-stopped`；省略 = 不改 */
  restartPolicy?: string
  /**
   * 内存上限（字节）；省略 = 不改。
   *
   * ⚠️ **`0` 会被后端拒为 `1000`**，它**不是**「取消限制」。
   * 实测 `docker update --memory=0` 返回 200 而限制根本没变
   * （守护进程对 update 是「字段非 0 才应用」）。
   * 因此 UI **不应提供**「取消内存限制」的开关——那是一个点了没反应又显示成功的按钮。
   */
  memoryLimitBytes?: number
  /** CPU 配额（十亿分之一核）；省略 = 不改。`0` 同样被拒 */
  nanoCpus?: number
}

/**
 * 热更新容器的重启策略与资源限制。
 *
 * **只支持真正能热改的字段**：镜像、端口、环境变量、挂载、网络改不了
 * （Docker 不支持），UI 不应给它们「保存」按钮。
 *
 * 返回**更新后实际生效**的值——可直接用它刷新界面。
 */
export function updateContainer(
  hostId: string,
  containerId: string,
  payload: UpdateContainerPayload
): Promise<ContainerUpdatedResult> {
  return request<ContainerUpdatedResult>({
    url: `${base(hostId)}/update`,
    method: 'post',
    data: { containerId, ...payload }
  })
}

/**
 * 复制容器（新建一个配置相同的兄弟容器）。
 *
 * **原容器不受影响**：不停止、不改名、不删除。因此确认提示不应带任何警告语气。
 *
 * `name` 省略时后端用「原名 + `-copy`」，被占用时自动递增 `-2`/`-3`——
 * 连点两次不会失败。
 *
 * ⚠️ 返回的 `recreated` 恒为 `false`，界面据此区分「复制」与「重建」。
 */
export function duplicateContainer(
  hostId: string,
  containerId: string,
  payload: { name?: string; start?: boolean } = {}
): Promise<ContainerRecreatedResult> {
  return request<ContainerRecreatedResult>({
    url: `${base(hostId)}/duplicate`,
    method: 'post',
    data: { containerId, ...payload }
  })
}

/**
 * 为已存在容器追加挂载（⚠️ **会重建容器**，容器 ID 一定变）。
 *
 * Docker 没有「给已存在容器加挂载」的接口，只能重建。因此：
 * - 二次确认**必须写明会重建**；
 * - 成功后旧容器以 `whalefleet-replaced-<短ID>` 保留（不是被删除），
 *   `replacedContainerId` 指到它——界面要提示用户它可以删，否则没人敢动。
 *
 * `start` 省略时后端**沿用原容器的运行状态**（不擅自启动一个本来没运行的容器）。
 *
 * 请求里给的是**要新增**的挂载，原有挂载全部保留；
 * 同一目标路径重复挂载 → `1000`。
 */
export function addContainerVolumes(
  hostId: string,
  containerId: string,
  volumes: Array<{ source: string; destination: string; readOnly?: boolean }>,
  start?: boolean
): Promise<ContainerRecreatedResult> {
  return request<ContainerRecreatedResult>({
    url: `${base(hostId)}/add-volumes`,
    method: 'post',
    data: { containerId, volumes, start }
  })
}
