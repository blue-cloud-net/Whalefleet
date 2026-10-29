/**
 * 与后端 docs/api-spec.md 的 ApiResult 契约对齐。
 * 后端返回 { success, code, message, data }；前端 axios 拦截器统一解包。
 */
export interface ApiResult<T = unknown> {
  success: boolean
  code: number
  message: string
  data: T | null
}

export interface ApiError {
  code: number
  message: string
  traceId?: string
  details?: Record<string, unknown>
}

/**
 * 业务错误码。
 *
 * **必须与后端 `Whalefleet.Application.Common.ErrorCodes` 及
 * `docs/api-spec.md` 的「当前已分配的错误码」表逐码一致**（含常量名）。
 *
 * 数值由后端定义，前端只做引用；新增错误码请先改 `api-spec.md`，再同步此处。
 * 禁止按数值分段自行推断含义——同一分段内 HTTP 语义并不统一。
 */
export const ErrorCode = {
  /** 成功 */
  Success: 0,

  // ===== 1000-1099 通用 =====
  /** 参数校验失败 */
  ValidationFailed: 1000,
  /** 资源不存在 */
  NotFound: 1001,
  /** 通用 Docker 操作失败（`DockerApiException`） */
  DockerOperationFailed: 1002,
  /** 操作超时（`TaskCanceledException`） */
  OperationTimeout: 1003,

  // ===== 1100-1199 鉴权与会话 =====
  /** 未认证：Token 缺失或无效 */
  Unauthenticated: 1100,
  /** Token 已过期 */
  TokenExpired: 1101,
  /** Refresh token 已撤销或无效 */
  RefreshTokenRevoked: 1102,
  /** 登录速率限制（5 次/分钟） */
  LoginRateLimited: 1103,
  /** Terminal token 无效或已过期（0.8.0） */
  TerminalTokenInvalid: 1104,

  // ===== 1200-1299 用户管理 =====
  /** 用户名已存在 */
  UsernameAlreadyExists: 1200,
  /** 用户不存在 */
  UserNotFound: 1201,
  /** 密码强度不满足要求 */
  WeakPassword: 1202,

  // ===== 1300-1399 主机管理 =====
  /** 主机不存在 */
  HostNotFound: 1300,
  /** 主机连通性失败 */
  HostUnreachable: 1301,
  /** 主机已停用 */
  HostDisabled: 1302,
  /** 主机名已被占用 */
  HostNameAlreadyExists: 1303,
  /** 分组名已被占用 */
  GroupNameAlreadyExists: 1304,
  /** 分组不存在 */
  GroupNotFound: 1305,
  /** 主机标签不存在（0.6.0） */
  HostTagNotFound: 1306,
  /** 主机标签名已被占用（0.6.0） */
  HostTagNameAlreadyExists: 1307,

  // ===== 1400-1499 资源操作 =====
  /** Docker 资源（容器 / 镜像 / Volume / Network）不存在 */
  DockerResourceNotFound: 1401,
  /** Docker API 调用失败 */
  DockerApiFailed: 1402,
  /** Docker 操作超时 */
  DockerTimeout: 1403,
  /** 资源当前状态不允许该操作（如不带 force 删除运行中的容器） */
  DockerResourceConflict: 1404,

  // ===== 1500-1599 资源权限 =====
  /** 无权限访问此资源 */
  ResourceForbidden: 1500,
  /** 无权限访问此主机 */
  HostForbidden: 1501,
  /** 无权限执行此操作（缺少 write 权限） */
  OperationForbidden: 1502,
  /** 授权记录不存在 */
  PermissionNotFound: 1503,
  /** 角色不存在 */
  RoleNotFound: 1510,
  /** 角色名已存在 */
  RoleNameAlreadyExists: 1511,
  /** 角色仍有分配在使用，不能删除 */
  RoleInUse: 1512,
  /** 内置角色不可修改（权限集或名称） */
  RoleProtected: 1513,
  /** 权限名不在内置目录里 */
  PermissionUnknown: 1514,
  /** 角色分配不存在 */
  RoleAssignmentNotFound: 1515,

  // ===== 1800-1899 仓库与镜像构建 / 推送 =====
  /** 仓库配置不存在 */
  RepositoryNotFound: 1800,
  /** 仓库别名已被占用 */
  RepositoryNameAlreadyExists: 1801,
  /** 仓库服务地址已被占用 */
  RepositoryAddressAlreadyExists: 1802,
  /** 未登记该地址的仓库凭据 */
  RepositoryCredentialMissing: 1803,
  /** 仓库拒绝了该凭据 */
  RepositoryAuthFailed: 1804,
  /** 仓库不可达 */
  RepositoryUnreachable: 1805,
  /** 镜像构建失败 */
  ImageBuildFailed: 1806,
  /** 镜像推送失败 */
  ImagePushFailed: 1807,

  // ===== 9000-9999 服务器内部错误 =====
  /** 服务器内部错误（未捕获异常） */
  InternalError: 9999
} as const

export type ErrorCodeValue = (typeof ErrorCode)[keyof typeof ErrorCode]

/** 主机可见性字段（用于 HostStore） */
export interface HostInfo {
  id: string
  name: string
  type: 'socket' | 'tcp'
  endpoint: string
  enabled: boolean
}

/** 主机连通状态（0.0.8 后端 /api/v1/host/list 返回前为本地占位） */
export type HostStatus = 'online' | 'offline' | 'unknown'

/** 单台主机的资源计数（列表端点不给；由 `getHostOverview()` 提供，未取到或主机离线时缺省） */
export interface HostCounts {
  /** 容器总数（含已停止） */
  containers: number
  /** 镜像总数 */
  images: number
  volumes: number
  networks: number
  /**
   * 运行中的容器数（后端 `/info` 的 `ContainersRunning`）。
   *
   * 与 `containers` 一起构成总览页的「运行 / 总数」——只给总数看不出这台机器是不是
   * 已经「挂了一半容器」。
   */
  containersRunning: number
  /** 已停止的容器数（后端 `/info` 的 `ContainersStopped`） */
  containersStopped: number
  /**
   * 健康检查通过的容器数。
   *
   * ⚠️ 与 `containersUnhealthy` 之和**通常小于** `containers`：绝大多数容器没配健康检查，
   * 「没配」既不是健康也不是不健康。
   */
  containersHealthy: number
  /** 健康检查失败的容器数 */
  containersUnhealthy: number
  /**
   * 镜像层占用的磁盘字节数（后端 `/system/df?type=image` 的 `TotalSize`）。
   *
   * `null` 表示**查不到**（守护进程不支持或调用失败），界面应显示占位符而不是「0」——
   * 0 字节会被读成「这台机器上没有镜像」。
   */
  imagesSizeBytes: number | null
}

/**
 * 主机分组。
 *
 * **是展示信息，不是权限边界**：「谁能操作哪台主机」由 0.1.0 落地的
 * 带作用域角色分配（`user_role_assignments`）回答。
 *
 * `name` 是管理员可改的数据（自定义分组直接展示）；三个内置分组由后端按英文标识种入
 * （`production` / `staging` / `local`），前端用 `host.group.<name>` 取 i18n 文案。
 * 映射见 `utils/group-display.ts`。
 */
export interface HostGroup {
  /** 分组主键（Guid 文本） */
  id: string
  /** 分组名（内置分组为英文标识，自定义分组为管理员输入） */
  name: string
  /** 描述；未填写时 null */
  description: string | null
  /** 排序权重（升序） */
  sortOrder: number
  /** 组内主机数（后端聚合，避免前端自己遍历） */
  hostCount: number
}

/**
 * 主机上的 Docker 版本信息。
 *
 * TODO(0.0.8)：由后端 `IDockerClient.GetVersionAsync()` 填充——
 * 对应 Version / ApiVersion / Os / Arch / GoVersion 五个字段。
 */
export interface HostVersion {
  /** Docker 引擎版本，如 `27.3.1` */
  version?: string
  /** Docker Engine API 版本，如 `1.47` */
  apiVersion?: string
  /** 操作系统，如 `linux` / `windows` */
  os?: string
  /** CPU 架构，如 `x86_64` / `aarch64` */
  arch?: string
  goVersion?: string
}

/**
 * 总览页用的主机视图：在 HostInfo 基础上补充连通状态、资源计数、版本、守护进程信息与标签。
 *
 * ⚠️ **列表端点只给 `status` 与 `tags`**：`counts` / `version` / `daemon` 要每台主机额外调守护进程，
 * 由 `POST /hosts/{hostId}/overview` 单独取（store 的 `loadHostDetails()`）。
 * 尚未取到、主机离线或该台请求失败时，这三个字段缺省（UI 显示「—」）。
 *
 * ⚠️ **没有 `startedAt`（0.6.0 移除）**：Docker Engine API 不提供守护进程启动时刻，
 * 也不提供宿主 boot time（`/info` 的 `SystemTime` 是**当前时间**），旧字段算出来恒为「已运行 0 秒」。
 */
export interface HostOverview extends HostInfo {
  status?: HostStatus
  counts?: HostCounts
  /** Docker 版本信息；离线主机取不到时为 undefined */
  version?: HostVersion
  /** 守护进程静态技术信息；离线或列表接口未取时为 undefined */
  daemon?: HostDaemon
  /** 挂在该主机上的标签（列表与概览都给；没有标签时为空数组） */
  tags?: HostTagRef[]
  /**
   * 预留：Agent 版本。
   *
   * Agent 模式排在 1.0.0 之后（见 docs/roadmap.md），当前后端不返回该字段，
   * 因此 UI 在无值时**不渲染任何 Agent 相关元素**。
   */
  agentVersion?: string
  /** 所属分组 id；undefined = 未分组 */
  groupId?: string
}

/**
 * 主页标签（管理页用）。
 *
 * **不参与授权**：授权边界只有带作用域的角色分配（分组与标签都不参与）。
 * 标签只用于总览过滤与打标 / 清标。
 */
export interface HostTag {
  /** 标签主键 */
  id: string
  /** 标签名（管理员输入，原样展示） */
  name: string
  /** 描述；未填写时 null */
  description: string | null
  /** 颜色（`#RRGGBB`）；未设置时 null（界面用默认色） */
  color: string | null
  /** 挂了该标签的主机数（后端聚合） */
  hostCount: number
}

/** 挂在某台主机上的标签（列表 / 概览用，不含描述与主机数） */
export interface HostTagRef {
  id: string
  name: string
  color: string | null
}

/** 守护进程已安装的插件（按类别分组） */
export interface HostDaemonPlugins {
  volume: string[]
  network: string[]
  log: string[]
  authorization: string[]
}

/**
 * 守护进程的静态技术信息（来自 `/info`）。
 *
 * ⚠️ `cpuCount` / `memoryBytes` 是**静态上限**，不是当前占用——
 * Docker Engine API 没有宿主机级实时指标端点，界面上不得标成「当前 CPU / 内存」。
 * ⚠️ `httpProxy` / `httpsProxy` 的 userinfo 已被服务端遮蔽。
 */
export interface HostDaemon {
  operatingSystem: string | null
  osVersion: string | null
  osType: string | null
  architecture: string | null
  kernelVersion: string | null
  cpuCount: number
  memoryBytes: number
  storageDriver: string | null
  storageDriverStatus: string[][]
  loggingDriver: string | null
  cgroupDriver: string | null
  cgroupVersion: string | null
  defaultRuntime: string | null
  runtimes: string[]
  initBinary: string | null
  containerdVersion: string | null
  runcVersion: string | null
  initVersion: string | null
  plugins: HostDaemonPlugins
  registryMirrors: string[]
  insecureRegistries: string[]
  indexServerAddress: string | null
  dockerRootDir: string | null
  securityOptions: string[]
  liveRestoreEnabled: boolean
  firewallBackend: string | null
  cdiSpecDirs: string[]
  httpProxy: string | null
  httpsProxy: string | null
  noProxy: string | null
  warnings: string[]
}

/**
 * 主机列表的过滤条件（服务端过滤）。
 *
 * ⚠️ **「未分组」用空串表达**：JSON 分不清「没给 groupId」与「给了 null」，
 * 因此约定「缺失 / null = 不过滤，空串 = 只看未分组」。
 */
export interface HostListFilter {
  /** 分组主键；空串 = 未分组；不传 = 不过滤 */
  groupId?: string
  /** 标签主键列表（任一命中）；空数组 = 不过滤 */
  tagIds?: string[]
  /** 连通状态：online / offline / unknown */
  status?: HostStatus
  /** 名称 / 端点模糊匹配（不区分大小写） */
  search?: string
}

/** 一类资源的磁盘用量汇总 */
export interface DiskUsageBucket {
  totalCount: number | null
  activeCount: number | null
  totalSize: number | null
  reclaimable: number | null
  items: DiskUsageItem[]
}

/** 一条磁盘用量明细 */
export interface DiskUsageItem {
  /** 镜像 ID / 容器 ID / 名 / 构建缓存记录 ID */
  id: string
  /** 可读名称；取不到时 null */
  name: string | null
  /** 占用字节数；守护进程未给出时 null */
  size: number | null
  /** 是否仍被引用；未知时 null */
  inUse: boolean | null
}

/**
 * 主机磁盘用量（`POST /hosts/{hostId}/system/df`）。
 *
 * **未查询的类为 null**：带 `type` 过滤时其余三类根本没统计，给 0 会被读成「确实没有」。
 */
export interface HostDiskUsage {
  images: DiskUsageBucket | null
  containers: DiskUsageBucket | null
  volumes: DiskUsageBucket | null
  buildCache: DiskUsageBucket | null
}

/** 当前登录用户 */
export interface CurrentUser {
  id: string
  username: string
  /**
   * 角色名列表（后端 `roles` 表）。
   *
   * 是**数组而非单值**：0.1.0 起一个用户可以被分配多个角色。
   *
   * 角色名声明为 `string` 而非字面量联合：角色是**数据**不是枚举，
   * 新增角色不应该需要改前端类型（0.0.8 起支持自定义角色）。
   *
   * ⚠️ 判断管理员**不要看这个列表里有没有 `admin`**——那只是角色名，
   * 真正决定的是权限集合（`permissions`）。只有当某个角色的权限集包含全部
   * 权限时，它才等价于管理员。见 auth store 的 `isAdmin`。
   */
  roles: string[]
  /**
   * **全局**有效权限名（不含任何主机作用域的权限）。
   *
   * 「全局」指不受主机作用域限制的权限（user.*、role.*、host.manage、audit.read 等）；
   * 某台主机上到底有哪些权限是按需计算的，不能一次性全塞进来——
   * 那需要一个用户的所有主机权限联表。
   */
  permissions: string[]
}

/**
 * 登录 / 刷新的返回体（对应后端 `LoginResponse`）。
 *
 * 刷新返回的结构与登录**完全一致**：后端每次刷新都会轮换刷新令牌，
 * 不存在"只返回 access token"的形态。因此调用方必须用返回值覆盖本地两个令牌，
 * 沿用旧刷新令牌会被服务端判定为重放（详见 docs/api-spec.md 的轮换语义表）。
 */
export interface LoginResult {
  accessToken: string
  refreshToken: string
  /** access token 剩余有效秒数，前端据此安排静默刷新 */
  expiresIn: number
  user: CurrentUser
}
/* ==================================================================
 * 容器（对应后端 `Whalefleet.Application.Containers`，0.0.9 已实现）
 *
 * 字段与后端投影**逐字对应**，不要在前端另起一套形状：
 * 后端已经把这些字段挑过一遍（而不是原样透传 SDK 对象），
 * 前端再改一次就等于凭空多出一层谁也说不清来源的转换。
 * ================================================================== */

/**
 * 容器状态（列表项用 `state` / `status` 两个文本字段，详情用这个结构体）。
 *
 * ⚠️ `state` 是 **Docker 的原始状态文本**，取值来自守护进程，**不在本项目控制范围内**。
 * 因此声明为 `string` 而不是字面量联合：新版本 Docker 加了新状态时不会让前端编译失败，
 * 未登记的状态由 `container-tone.ts` 统一落到「未知」色。
 */
export interface ContainerState {
  /** 状态文本，如 `running` / `exited` / `paused` */
  status: string
  running: boolean
  paused: boolean
  restarting: boolean
  /** 本次启动时间（ISO 8601）；从未启动过为 null */
  startedAt: string | null
  /** 本次结束时间（ISO 8601）；仍在运行或从未结束为 null */
  finishedAt: string | null
  /** 退出码；**运行中恒为 null**（后端会把运行中的固定 0 抹成 null，见其类型注释） */
  exitCode: number | null
  oomKilled: boolean
  /** 守护进程累计的重启次数 */
  restartCount: number
  /**
   * 健康检查状态。
   *
   * ⚠️ **未配健康检查是 `none`，不是 null**：「没配」永远不会变，「查不到」会变，
   * 混成一个值就没法在界面上分开表达。
   */
  healthStatus: ContainerHealthStatus
}

/**
 * 容器健康检查状态（后端 `ContainerHealthStatus` 的小驼峰序列化）。
 *
 * 仅用于封闭集合：Docker 只给这四种。
 */
export type ContainerHealthStatus = 'none' | 'starting' | 'healthy' | 'unhealthy'

/** 单个容器的健康与退出原因（`POST .../containers/health` 的条目） */
export interface ContainerHealthSummary {
  /** 完整容器 ID */
  containerId: string
  /** 容器名（无前导斜杠） */
  name: string
  healthStatus: ContainerHealthStatus
  restartCount: number
  /** 退出码；运行中为 null */
  exitCode: number | null
  oomKilled: boolean
  running: boolean
}

/** 批量健康查询结果；**查不到的容器直接缺席**（多半是被删掉了） */
export interface ContainerHealthResult {
  items: ContainerHealthSummary[]
}

/** 端口映射（`publicPort` / `ip` 为 null 表示「容器内端口已暴露但未映射到宿主」） */
export interface ContainerPort {
  privatePort: number
  publicPort: number | null
  type: string
  ip: string | null
}

/** 容器列表项 */
export interface ContainerSummary {
  /** 完整容器 ID */
  id: string
  /** 容器名；后端已去掉 Docker 内部的前导斜杠 */
  name: string
  /** 镜像名，如 `nginx:latest` */
  image: string
  /** 镜像 ID（`sha256:…`） */
  imageId: string
  /** 启动命令 */
  command: string
  /** 创建时间（ISO 8601） */
  created: string
  /** Docker 原始状态文本，如 `running` / `exited` */
  state: string
  /** 人类可读状态，如 `Up 3 hours`（后端原样给出，不本地翻译——各语言版本的措辞不同） */
  status: string
  ports: ContainerPort[]
}

/** 容器列表的分页结果 */
export interface ContainerListResult {
  items: ContainerSummary[]
  /** 分页前的总条数 */
  total: number
  /** 服务端实际使用的页码（从 0 开始） */
  pageIndex: number
  /** 服务端实际使用的每页条数（**已夹紧**：请求 5000 会回 100） */
  pageSize: number
}

/** 容器启动配置（详情页用；列表项不带它——`env` 常含密码 / Token） */
export interface ContainerConfigDetail {
  image: string
  cmd: string[]
  entrypoint: string[]
  env: string[]
  labels: Record<string, string>
  workingDir: string
  hostname: string
  user: string
  tty: boolean
}

/** 容器宿主侧配置（详情页用） */
export interface ContainerHostConfigDetail {
  /** Docker 线上写法：`no` / `always` / `on-failure` / `unless-stopped` */
  restartPolicy: string
  autoRemove: boolean
  networkMode: string
  privileged: boolean
  /** 字节；未限制为 null（Docker 用 0 表示未限制，后端转成 null） */
  memoryLimitBytes: number | null
  /** 十亿分之一核；未限制为 null */
  nanoCpus: number | null
}

/** 容器挂载 */
export interface ContainerMountDetail {
  /** `bind` / `volume` / `tmpfs` */
  type: string
  source: string
  destination: string
  readWrite: boolean
}

/** 容器网络接入信息 */
export interface ContainerNetworkDetail {
  name: string
  ipAddress: string
  gateway: string
  macAddress: string
}

/** 容器详情（对应后端 inspect 的精选投影） */
export interface ContainerDetail {
  id: string
  name: string
  created: string
  /** 镜像名（后端取 `Config.Image`，不是 inspect 顶层的镜像 ID） */
  image: string
  state: ContainerState
  config: ContainerConfigDetail
  hostConfig: ContainerHostConfigDetail
  mounts: ContainerMountDetail[]
  networks: ContainerNetworkDetail[]
  /**
   * 端口映射（已按确定性顺序排列，与列表端点同口径）。
   *
   * ⚠️ **详情接口也带它**是为了「编辑并重建」能回填端口编辑器：
   * 表单里空着而重建时又整份替换，用户会在「只改了个环境变量」的动作里丢掉全部端口映射。
   */
  ports: ContainerPort[]
}

/** 容器日志（**整段文本**，不是行数组——见后端类型注释） */
/**
 * 展示用的容器日志行。
 *
 * `seq` 是**展示序号**（从 1 起，等于它在列表里的位置），
 * 向上加载更早的日志后会重新编号。
 */
export interface ContainerLogLine {
  /** 展示序号（从 1 起） */
  seq: number
  /** 该行的时间戳（ISO 8601）；守护进程未提供时为 null */
  timestamp: string | null
  /** 日志正文（不含时间戳前缀） */
  line: string
}

/**
 * Hub 推送的容器日志行。
 *
 * ⚠️ `seq` 是**流内序号**（服务端那条订阅里的序号），**不是展示序号**。
 * 它的用途是**检测丢行**：服务端频道拥塞时会丢最旧的行，
 * 丢掉的行已经占用了序号 → 下一批的首行序号会跳号。
 */
export interface ContainerLogStreamLine {
  /** 流内序号（服务端分配） */
  seq: number
  /** 该行的时间戳（ISO 8601）；守护进程未提供时为 null */
  timestamp: string | null
  /** 日志正文（不含时间戳前缀） */
  line: string
}

/**
 * Hub 推送的一批日志行（SignalR `OnLog` 的载荷）。
 */
export interface ContainerLogPayload {
  containerId: string
  lines: ContainerLogStreamLine[]
}

/**
 * 容器日志（0.5.0 起结构化）。
 *
 * ⚠️ **破坏性变更**：0.5.0 之前是 `{ containerId, content }`（一整段文本），
 * 现在是行数组。理由见后端 `ContainerLogsResult` 的说明。
 */
export interface ContainerLogsResult {
  containerId: string
  /** 日志行（按时间顺序，序号从 1 起） */
  lines: ContainerLogLine[]
  /** 是否可能还有更早的日志未返回（前端据此决定要不要显示「向上加载更多」） */
  truncated: boolean
}

/**
 * 交互式终端的短时令牌（0.8.0）。
 *
 * ⚠️ **不是 access token**：它 5 分钟过期，只对一条 WebSocket
 * （`/ws/hosts/{hostId}/containers/exec`）有效，且**不被任何 HTTP 接口接受**——
 * 服务端给它签的是独立 audience，主鉴权在校验 audience 时就会拒绝它。
 */
export interface ContainerTerminalToken {
  /** 令牌原文，放进 WebSocket 的 `?token=` */
  terminalToken: string
  /** 过期时间（ISO 8601） */
  expiresAt: string
}

/**
 * 一次容器资源采样（SignalR `OnStats` 的载荷）。
 *
 * CPU 百分比由服务端算好（相邻两帧相减），因此所有客户端口径一致。
 */
export interface ContainerStatsSample {
  containerId: string
  /** CPU 占用百分比（0–100×核数） */
  cpuPercent: number
  /** 内存占用字节数 */
  memoryUsage: number
  /** 内存上限字节数；守护进程未设限时为 0 */
  memoryLimit: number
  /** 累计接收字节数 */
  networkRxBytes: number
  /** 累计发送字节数 */
  networkTxBytes: number
  /** 接收速率（字节/秒）；首帧为 null（速率要由相邻两帧相减得到） */
  networkRxBytesPerSecond: number | null
  /** 发送速率（字节/秒）；首帧为 null */
  networkTxBytesPerSecond: number | null
  /** 采样时刻（ISO 8601） */
  timestamp: string
}

/**
 * 订阅时补发的一段历史采样（SignalR `OnStatsHistory`）。
 *
 * 顺序是**旧 → 新**：客户端直接追加即可，最新的一条同时就是「当前值」。
 */
export interface ContainerStatsHistoryPayload {
  containerId: string
  samples: ContainerStatsSample[]
}

/**
 * Hub 推送的一次资源采样（SignalR `OnStats` 的载荷）。
 */
export interface ContainerStatsPayload {
  containerId: string
  stats: ContainerStatsSample
}

/**
 * 容器异常退出通知（SignalR `OnContainerDied` 的载荷）。
 */
export interface ContainerDiedEvent {
  hostId: string
  containerId: string
  containerName: string
  exitCode: number
  /** 是否因内存不足被杀死（OOM） */
  outOfMemory: boolean
  occurredAt: string
}

/**
 * 镜像操作进度（SignalR `OnPullProgress` 的载荷）。
 */
export interface ImageOperationProgress {
  /** 进度令牌（与订阅时用的同一个） */
  token: string
  /** 操作类型：`pull` / `build` / `push` */
  operation: string
  /** 总体进度百分比（0–100）；**无法确定时为 null**（不是 0） */
  percent: number | null
  /** 最近一条状态原文 */
  status: string
  /** 操作是否已成功结束 */
  done: boolean
  /** 失败原因；成功时为 null */
  error: string | null
}

/**
 * 实时通道的错误通知（SignalR `OnStreamError` 的载荷）。
 */
export interface RealtimeError {
  /** 业务错误码 */
  code: number
  /** 可展示的消息 */
  message: string
}

/** 创建容器的结果 */
export interface ContainerCreatedResult {
  containerId: string
  /** 守护进程给的提示（如「端口未映射」）；无提示时为空数组。**不要当错误处理**。 */
  warnings: string[]
}

/* ==================================================================
 * 容器基础操作（0.3.0）
 * ================================================================== */

/** 重命名结果（回带新名，省掉一次 inspect）。 */
export interface ContainerRenamedResult {
  containerId: string
  name: string
}

/**
 * 热更新结果——**回带更新后实际生效的值**，不是请求里写的值。
 *
 * ⚠️ 后端在更新前先 inspect 一次、把未改的字段按当前值回填，再回带最终结果。
 * 因此这里永远可以拿它刷新界面，而不必自己猜「没传的字段是什么值」。
 *
 * `memoryLimitBytes` / `nanoCpus` 为 `null` 表示**未限制**（不是 0）。
 */
export interface ContainerUpdatedResult {
  containerId: string
  /** `no` / `always` / `on-failure` / `unless-stopped` */
  restartPolicy: string
  memoryLimitBytes: number | null
  nanoCpus: number | null
}

/**
 * 重建结果（复制与追加挂载共用）。
 *
 * ⚠️ **`recreated` 是界面必须看的字段**：
 * - `false`（复制）：原容器毫发无损，提示语应是「已复制出新容器」。
 * - `true`（追加挂载）：**容器被重建了**，ID 会变，旧容器被让位保留。
 *
 * `replacedContainerId` 非空时，界面上那个「来历不明的容器」就是它——
 * 应当显式提示用户「可以删除」，否则没人敢动它。
 */
export interface ContainerRecreatedResult {
  containerId: string
  name: string
  /** 被让位（改名保留）的旧容器 ID；复制场景为 null */
  replacedContainerId: string | null
  running: boolean
  warnings: string[]
  recreated: boolean
}

/* ==================================================================
 * 镜像（对应后端 `Whalefleet.Application.Images`，0.0.10 已实现）
 * ================================================================== */

/**
 * 镜像列表项。
 *
 * `repoTags` 为**空数组**表示悬挂镜像（Docker 内部用 `<none>:<none>` 占位，
 * 后端已把它过滤掉）——界面要显示「`<none>`」而不是一行空白，
 * 并且仍允许按 ID 删除。
 */
export interface ImageSummary {
  /** 完整镜像 ID（`sha256:…`） */
  id: string
  repoTags: string[]
  repoDigests: string[]
  /** 创建时间（ISO 8601） */
  created: string
  /** 字节 */
  size: number
  /** 与其他镜像共享的字节数 */
  sharedSize: number
  /** 引用该镜像的容器数 */
  containerCount: number
}

export interface ImageListResult {
  items: ImageSummary[]
  total: number
  pageIndex: number
  pageSize: number
}

/**
 * 镜像详情（后端 inspect 的**精选投影**）。
 *
 * ⚠️ `env` 与 `labels` 可能含敏感值（有人把构建参数写进 `ENV` / `LABEL`），
 * 因此详情页要给出提示；列表项刻意不带这些字段。
 */
export interface ImageDetail {
  id: string
  repoTags: string[]
  repoDigests: string[]
  created: string
  size: number
  architecture: string
  os: string
  /** 仅 Windows 镜像有值 */
  osVersion: string
  author: string
  comment: string
  env: string[]
  cmd: string[]
  entrypoint: string[]
  /** 形如 `80/tcp`（后端已去重并去掉空对象） */
  exposedPorts: string[]
  /** 形如 `/var/cache/nginx` */
  volumes: string[]
  labels: Record<string, string>
  workingDir: string
  layerCount: number
}

/** 拉取镜像的结果（后端在拉取后额外 inspect 一次，因此直接带出大小与标签） */
export interface ImagePullResult {
  imageId: string
  /** 完整引用，如 `nginx:latest` */
  reference: string
  size: number
  repoTags: string[]
}

/**
 * 删除镜像的结果。
 *
 * `untagged` 非空是常态：一个镜像 ID 常挂多个标签，删掉其中一个时镜像本体仍在。
 * 界面必须把被解绑的标签告诉用户，否则会出现「我删了镜像，列表里另一个标签还在」的困惑。
 */
export interface ImageRemovedResult {
  untagged: string[]
  deleted: string[]
}

/**
 * 打标签的结果。
 *
 * ⚠️ **`imageId` 与源镜像是同一个 ID**——`docker tag` 不复制镜像，只是加一个引用。
 * 因此界面应把这一行更新成「同一镜像的另一个标签」，而**不是**新增一行镜像：
 * 后者会让用户以为副本可以随便删（删掉最后一个标签时镜像确实会消失）。
 */
export interface ImageTaggedResult {
  imageId: string
  /** 新建的完整引用，如 `registry.example.com/team/app:v1.2.3` */
  reference: string
  /** 该镜像 ID 上的全部标签（打标签后已变化） */
  repoTags: string[]
}

/**
 * 镜像构建的结果（0.4.0）。
 *
 * ⚠️ `buildLog` **必须展示**：构建失败时守护进程只给一行
 * `returned a non-zero code: 1`，真正的原因（`COPY failed: no such file`）
 * 在它上面几百行里。后端保留**最后** 500 行并随响应带回，排错的现场在用户手里。
 */
export interface ImageBuildResult {
  imageId: string
  /** 构建产物的完整引用，如 `myapp:v1` */
  reference: string
  size: number
  repoTags: string[]
  /** 构建输出（最多 500 行，失败时含 `ERROR:` 行） */
  buildLog: string[]
}

/**
 * 推送镜像的结果（0.4.0）。
 *
 * ⚠️ `authenticated` 是**关键信息**：匿名推送同样能成功（公开仓库）。
 * 只看「推送成功」无法区分「用的是我配的凭据」与「这个仓库压根不要凭据」——
 * 后者往往意味着地址配错了（推到了一个意外的公开仓库）。
 */
export interface ImagePushResult {
  imageId: string
  /** 推送时使用的完整引用 */
  reference: string
  /** 实际推到的仓库地址（`host` 或 `host:port`） */
  repositoryServerAddress: string
  /** 本次是否带了凭据 */
  authenticated: boolean
  /** 推送输出 */
  pushLog: string[]
}

/* ==================================================================
 * 私有仓库配置（对应后端 `Whalefleet.Application.Repositories`，0.4.0）
 * ================================================================== */

/**
 * 仓库配置的列表项。
 *
 * ⚠️ **响应里既没有口令，也没有口令密文**（连密文都能当钥匙用）。
 * 界面要改口令时只能重新输入——所以本版**没有「编辑」功能**，
 * 改口令 = 删掉重加（后端也只有 `add` / `remove` / `test`）。
 *
 * `hasCredentials` 回答的是「这条配置带不带账号」，不是「口令是什么」。
 */
export interface RepositorySummary {
  id: string
  /** 别名（管理员可读的名字），全表唯一 */
  name: string
  /** 规范化后的服务地址（`host` 或 `host:port`，无 scheme、无尾斜杠） */
  serverAddress: string
  /** 登录账号；匿名配置为空串 */
  username: string
  hasCredentials: boolean
  description: string | null
  createdAt: string
  lastModifiedAt: string | null
}

/**
 * 仓库连通性测试的结果。
 *
 * **`reachable` 与 `authenticated` 是两个独立的布尔值**，不能压成一个：
 * Registry 的三种常见状态「网络不通」「连得上但没给凭据」「连得上且凭据有效」
 * 需要三种不同的处置建议（查网络 / 补凭据 / 无需动作）。
 */
export interface RepositoryTestResult {
  reachable: boolean
  authenticated: boolean
  message: string
}

/* ==================================================================
 * 批量操作（0.1.0，容器与镜像共用同一形状）
 * ================================================================== */

/**
 * 批量操作里单个资源的失败详情。
 *
 * `code` 与单条端点抛出的错误码**完全一致**（如 `1404`），
 * 因此界面可以复用同一套文案映射，不必为批量场景另写一张错误码表。
 */
export interface BatchOperationFailure {
  /** 失败的资源标识（容器 ID / 镜像引用），与请求里给出的形式一致 */
  resourceId: string
  code: number
  message: string
}

/**
 * 批量操作的汇总结果。
 *
 * **部分成功也是 200**：`succeeded + failures.length` 恒等于 `requested`（服务端已去重），
 * 而 `requested` 是**去重后**的个数——传两次同一个容器不会显示成「成功 1、失败 1」。
 */
export interface BatchOperationResult {
  /** 去重后的资源个数 */
  requested: number
  succeeded: number
  failures: BatchOperationFailure[]
}

/* ==================================================================
 * 数据卷与网络（0.2.0）
 * ================================================================== */

/**
 * 数据卷列表项（对应后端 `VolumeSummary`）。
 *
 * ⚠️ **`size` / `refCount` 为 `null` 表示「不知道」，`0` 是一个确切的答案**。
 * 用量来自守护进程的 `GET /system/df`（**不是**列表接口——`GET /volumes` 的每一项里
 * 没有 `UsageData`）。查询失败或驱动不报用量时后端给 `null`，界面显示「—」；
 * 空卷 / 没人引用则是真正的 `0`，应当如实显示成「0 B」/「0」。
 */
export interface VolumeSummary {
  name: string
  driver: string
  /** 宿主机挂载路径；仅 `local` 驱动有值 */
  mountpoint: string
  scope: string
  /** ISO 8601；守护进程未提供时为 `null` */
  created: string | null
  /** 字节；`null` = 守护进程没给用量（含义不明），`0` = 确实是空的 */
  size: number | null
  /** 引用它的容器数；`null` = 守护进程没给，`0` = 确实没人引用 */
  refCount: number | null
  labels: Record<string, string>
}

/**
 * 数据卷列表结果（服务端内存分页，口径与容器 / 镜像一致）。
 *
 * ⚠️ **`warnings` 必须单独处理**：守护进程会在响应里给出「某卷的驱动没装」之类的告警。
 * 只看 `items` 会让这些告警静默丢失，用户看到的列表就是「少了几个卷」却不知道为什么。
 *
 * ⚠️ **`total` 不能用 `items.length` 代替**：那样只在「只有一页」时正确，
 * 翻页后页脚会显示成「共 20 个」。
 */
export interface VolumeListResult {
  items: VolumeSummary[]
  /** 分页前的总条数 */
  total: number
  /** 服务端实际使用的页码（从 0 开始） */
  pageIndex: number
  /** 服务端实际使用的每页条数（**已夹紧**：请求 5000 会回 100） */
  pageSize: number
  warnings: string[]
}

/**
 * 数据卷详情（对应后端 `VolumeDetail`）。
 *
 * ⚠️ **刻意没有 `size` / `refCount`**：inspect 响应里没有用量信息（那来自列表接口），
 * 后端不会塞两个恒为 0 的字段进来伪造精度。详情页要显示用量得回到列表数据。
 */
export interface VolumeDetail {
  name: string
  driver: string
  mountpoint: string
  scope: string
  created: string | null
  /** 驱动选项（如 `local` 的 `device` / `o=bind`）；详情页的主要价值所在 */
  options: Record<string, string>
  labels: Record<string, string>
}

/**
 * 网络列表项（对应后端 `NetworkSummary`）。
 *
 * ⚠️ **没有「容器数」字段，这是刻意的**：Docker 的列表接口不返回 `Containers`
 * （只有详情接口返回，已实测），补一个进去必然恒为 0，
 * 而界面上「0 个容器」会被读成「这个网络没在用」——可能正相反。
 * 真实的接入情况请打开详情看 `containers`。
 */
export interface NetworkSummary {
  /** 裸的 64 位十六进制，**不带 `sha256:` 前缀**（镜像 ID 才有前缀） */
  id: string
  name: string
  driver: string
  scope: string
  /** 是否可被 `docker run --network` 单独接入 */
  attachable: boolean
  /** 是否为内部网络（容器之间才能互通） */
  internal: boolean
  /** CIDR；自定义驱动（如 `macvlan`）可能为空数组 */
  subnets: string[]
  /** 与 `subnets` 一一对应；守护进程未分配时该位置为空串 */
  gateways: string[]
  labels: Record<string, string>
}

export interface NetworkListResult {
  items: NetworkSummary[]
  /** 分页前的总条数（不能用 `items.length` 代替，翻页后它会变成页大小） */
  total: number
  /** 服务端实际使用的页码（从 0 开始） */
  pageIndex: number
  /** 服务端实际使用的每页条数（**已夹紧**：请求 5000 会回 100） */
  pageSize: number
}

/** 网络详情（对应后端 `NetworkDetail`）。 */
export interface NetworkDetail {
  id: string
  name: string
  driver: string
  scope: string
  attachable: boolean
  internal: boolean
  enableIPv4: boolean
  enableIPv6: boolean
  ipam: NetworkIpam[]
  /** 已接入的容器端点（含各自 IP）——判断「谁在用这个网络」的唯一权威数据 */
  containers: NetworkContainerEndpoint[]
  options: Record<string, string>
  labels: Record<string, string>
}

/** 网络的单个 IPAM 地址池；三项都可能为空。 */
export interface NetworkIpam {
  subnet: string | null
  gateway: string | null
  /** 动态分配范围；未指定时为 `null`（不是空串） */
  ipRange: string | null
}

/** 已接入某网络的容器端点。 */
export interface NetworkContainerEndpoint {
  id: string
  name: string
  macAddress: string | null
  /** 含前缀长度，如 `172.18.0.2/16` */
  ipv4Address: string | null
  ipv6Address: string | null
}

/* ==================================================================
 * 角色与权限（0.1.0，对应后端 `Whalefleet.Application.Permissions`）
 * ================================================================== */

/**
 * 权限名（后端 `Whalefleet.Core.Permissions.Permission`）。
 *
 * 声明为 `string` 而非字面量联合：权限名是**数据**不是枚举，
 * 后端新增权限不应该让前端编译失败。判断是否具备某个权限请用
 * `permissions.includes(name)`，而不是相等比较。
 */
export type Permission = string

/**
 * 权限的作用域。
 *
 * 决定了「在哪个 hostId 上求值这个权限」：
 * - `global`：与主机无关，全局分配就生效
 * - `host`：随主机变化，同一个用户可以在 A 机是写、在 B 机只读
 * - `container`：0.12.0 落地，当前后端尚未启用
 */
export type PermissionScope = 'global' | 'host' | 'container'

/**
 * 一个可授予的权限项（对应后端 `PermissionDefinition`）。
 *
 * 后端自带全部权限的目录，界面**直接遍历它**渲染矩阵，
 * 不要在前端硬编码一份权限列表——那份副本必然与后端不同步。
 */
export interface PermissionDefinition {
  /** 权限名，如 `container.write` */
  name: Permission
  /** 资源域（`host` / `container` / `image` / `network` / `volume` / `user` / `role` / `audit`） */
  domain: string
  /** 档位（`read` / `write` / `admin` / `manage`） */
  level: string
  /** 作用域 */
  scope: PermissionScope
}

/**
 * 角色列表项（对应后端 `RoleSummary`，字段逐字对应）。
 *
 * ⚠️ 后端 `list` 端点返回的是**普通数组**而不是分页信封，因此这里没有
 * `items` / `total` / `pageIndex` / `pageSize`——角色是人工维护的个位数集合，
 * 分页只会给每个调用点多一层要拆的壳。
 */
export interface RoleSummary {
  id: string
  name: string
  /** 可读名称；为空时界面回退显示 `name` */
  displayName: string | null
  description: string | null
  /** 内置角色：不可删除，且权限集不可修改 */
  isBuiltIn: boolean
  /** 新建用户时的默认角色（全库至多一个） */
  isDefault: boolean
  /** 权限名列表（有序，由弱到强；顺序由后端权限目录决定） */
  permissions: Permission[]
  /** 当前**有效**分配数（全局 + 主机）；非 0 时不允许删除角色 */
  assignmentCount: number
}

/**
 * 一条角色分配（对应后端 `RoleAssignmentSummary`，字段逐字对应）。
 *
 * 带 `username` / `role` / `hostName` 而不只是主键：
 * 管理界面要回答的是「**谁**以**什么角色**管**哪台机器**」，
 * 三个名字比三个 GUID 有用得多。
 */
export interface RoleAssignmentSummary {
  id: string
  userId: string
  username: string
  roleId: string
  /** 角色标识（后端字段名就是 `role`，不是 `roleName`） */
  role: string
  /** 角色可读名称；未设置时为 null，界面回退显示 `role` */
  roleDisplayName: string | null
  /** `null` = 全局分配（对所有主机生效，但只贡献角色的全局权限） */
  hostId: string | null
  /** `hostId` 对应的主机名；全局分配为 null */
  hostName: string | null
  lastModificationTime: string | null
}

/**
 * 角色定位条件（对应后端 `RoleSelectorRequest`）。
 *
 * `roleId` 与 `role` **至少给一个**：给主键是「从列表里选中一行」的场景，
 * 给名字是「已知角色标识」的场景——后者不必先查一遍 GUID。
 */
export interface RoleSelectorRequest {
  roleId?: string
  role?: string
}

/**
 * 分配角色的请求体（对应后端 `AssignRoleRequest`）。
 *
 * `host` **省略或为 null 即全局分配**；它只在用户视角的端点里出现——
 * 主机视角的作用域固定在 URL 上，请求体里带 host 只会多一层可能写错的表达。
 */
export interface AssignRoleRequest extends RoleSelectorRequest {
  userId?: string
  host?: string | null
}

/** 撤销角色分配的请求体（对应后端 `RevokeRoleAssignmentRequest`）。 */
export interface RevokeRoleAssignmentRequest extends RoleSelectorRequest {
  userId?: string
  host?: string | null
}

/* ==================================================================
 * 主机管理（admin，对应后端 `Whalefleet.Application.Hosts`）
 * ================================================================== */

/** 主机认证方式（**只有方式，不含任何凭据内容**） */
export type HostAuthKind = 'none' | 'tls' | 'basic'

/**
 * 管理端点返回的主机对象。
 *
 * 与展示用的 `HostOverview` 有两处关键差别，不要互相替代：
 * - `id` 是**主键**（Guid，用于权限外键与审计）；`HostOverview.id` 是**主机名**（等于路径里的 hostId）
 * - `reachable` 是可空布尔：`null` = **未探测**（create / update 不探活，写操作不该被网络超时拖住）。
 *   把它当 `false` 会把「没测」显示成「离线」。
 */
export interface AdminHostSummary {
  /** 主键（Guid） */
  id: string
  /** 主机名——对外标识，也是路径里的 hostId */
  name: string
  type: 'socket' | 'tcp'
  endpoint: string
  enabled: boolean
  reachable: boolean | null
  authKind: HostAuthKind
  /** 所属分组主键；null = 未分组 */
  groupId: string | null
}

/** TLS 凭据输入（三项路径均必填） */
export interface TlsConfigInput {
  caCertificatePath: string
  clientCertificatePath: string
  clientKeyPath: string
  /** 私钥加密口令；未加密时留空 */
  clientKeyPassword?: string
}

/** Basic 凭据输入 */
export interface BasicAuthInput {
  username: string
  password: string
}

/** 创建主机的请求体（`tlsConfig` 与 `basicAuth` 互斥，都省略即无认证） */
export interface CreateHostPayload {
  name: string
  type: 'socket' | 'tcp'
  endpoint: string
  tlsConfig?: TlsConfigInput
  basicAuth?: BasicAuthInput
}

/**
 * 更新主机的请求体。
 *
 * **不含 `name`**：主机名是对外标识（路径里的 hostId 就是它），改名会让已发放的深链失效。
 * `clearCredentials` 用于「改回无认证」——JSON 分不清「没给 tlsConfig」与「tlsConfig: null」，
 * 没有它就没有表达方式（详见 docs/api-spec.md）。
 */
export interface UpdateHostPayload {
  endpoint?: string
  tlsConfig?: TlsConfigInput
  basicAuth?: BasicAuthInput
  clearCredentials?: boolean
  enabled?: boolean
}

/** 测试未保存配置的请求体（刻意不带 hostId） */
export interface TestHostPayload {
  type: 'socket' | 'tcp'
  endpoint: string
  tlsConfig?: TlsConfigInput
  basicAuth?: BasicAuthInput
}

/* ==================================================================
 * 用户管理（admin，对应后端 `Whalefleet.Application.Users`）
 * ================================================================== */

/**
 * 用户列表项。
 *
 * `roles` 是**角色名数组**：0.1.0 起用户与角色是多对多，
 * 一个用户可以同时有多个角色。
 */
export interface AdminUserSummary {
  id: string
  username: string
  roles: string[]
  /** 创建时间（ISO 8601） */
  creationTime: string
}

export interface UserListResult {
  items: AdminUserSummary[]
  total: number
  pageIndex: number
  pageSize: number
}

/* ==================================================================
 * 主机分组（0.0.13）
 * ================================================================== */

/**
 * 主机分组。
 *
 * **是展示信息，不是权限边界**：「谁能操作哪台主机」由 0.1.0 落地的
 * 带作用域角色分配（`user_role_assignments`）回答。
 *
 * `name` 是管理员输入的数据（自定义分组直接展示）；内置的三个分组由后端种入固定名
 * （`production` / `staging` / `local`），前端用 `host.group.<name>` 取 i18n 文案，
 * 因此内置分组在中英文界面下都能正确显示，而自定义分组按用户输入原样展示
 * （用户数据不翻译——把管理员输入的「预发布」硬翻成英文只会更难懂）。
 */
export interface HostGroupSummary {
  id: string
  name: string
  description: string | null
  sortOrder: number
  /** 组内主机数（后端聚合，避免前端自己数） */
  hostCount: number
}

/* ==================================================================
 * 资源清理（0.7.0）
 * ================================================================== */

/**
 * 清理（prune）的结果。
 *
 * ⚠️ **它是「实际发生了什么」，不是「将要发生什么」**：
 * 预览（`POST /hosts/{hostId}/system/df`）是**策略级**的候选清单，
 * 而守护进程在真正删除时还会跳过带挂载选项的卷、按自己的引用计数判断——
 * 两者可能不一致。因此界面应当**展示这个清单**，而不是只显示一句「清理完成」。
 *
 * ⚠️ **`deleted` 里的标识形态按资源而异**：镜像是标签（`nginx:latest`），
 * 悬空镜像没有标签时退回镜像 ID；卷是卷名。
 */
export interface PruneResult {
  /** 被删除的资源标识（镜像标签 / 镜像 ID / 卷名） */
  deleted: string[]
  /** 实际回收的字节数；由守护进程给出，不是本地估算 */
  reclaimedBytes: number
}


// ==================== 操作审计（0.9.0） ====================

/**
 * 一次操作的结果。
 *
 * `denied` 与 `failure` 不是一回事：前者是**规则**挡住（权限不足、限流），
 * 资源没被碰过；后者是规则放行了但**执行**出错（守护进程不可达、容器已不存在）。
 * 现场处置的往往是不同的人。
 */
export type AuditOutcome = 'success' | 'denied' | 'failure'

/**
 * 一条审计记录。
 *
 * ⚠️ `actorName` / `targetName` / `hostName` 都是**写入时的快照**：
 * 用户与容器会被删除、主机名会被改，而审计要能在那之后仍然读得懂。
 * 因此它们**不随资源变化**，这正是「删了容器还能看到当时的容器名」的实现方式。
 */
export interface AuditLogEntry {
  id: string
  /** 发生时刻（ISO 8601，UTC） */
  occurredAt: string
  /** 动作标识（如 `container.start`）；界面按映射表翻译，未命中时原样显示 */
  action: string
  /** 操作者名（快照） */
  actorName: string
  /** 目标类型（`container` / `image` / …）；无目标为 null */
  targetType: string | null
  /** 目标名（快照） */
  targetName: string | null
  /** 主机名（快照） */
  hostName: string | null
  outcome: AuditOutcome
  /** 失败时的业务错误码 */
  errorCode: number | null
  clientIp: string | null
  detail: string | null
}

/** 审计日志分页结果。 */
export interface AuditLogListResult {
  items: AuditLogEntry[]
  /** 满足条件的总条数 */
  total: number
  /** 当前页码（从 0 开始） */
  pageIndex: number
  /** 每页条数（服务端夹紧后的实际值） */
  pageSize: number
}

/**
 * 审计日志查询条件。
 *
 * 时间省略时服务端取**最近 7 天**——审计表只增不减，不设窗口的查询会随运行时间变慢。
 */
export interface AuditLogQuery {
  /** 起始时刻（ISO 8601，含） */
  from?: string
  /** 结束时刻（ISO 8601，含） */
  to?: string
  /** 操作者名关键字 */
  actor?: string
  /** 动作标识（精确匹配） */
  action?: string
  /** 目标类型（精确匹配） */
  targetType?: string
  /** 结果筛选 */
  outcome?: AuditOutcome
  /** 自由文本（目标名 / 主机名 / 说明） */
  search?: string
  pageIndex?: number
  pageSize?: number
}
