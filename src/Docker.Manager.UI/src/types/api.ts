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
 * **必须与后端 `Docker.Manager.Application.Common.ErrorCodes` 及
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
  /** Terminal token 无效或已过期（0.4.0） */
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

/** 单台主机的资源计数（后端聚合接口就绪前为 undefined，UI 显示占位） */
export interface HostCounts {
  containers: number
  images: number
  volumes: number
  networks: number
}

/**
 * 主机分组。
 *
 * 当前为**仅展示**语义：组定义是前端常量，没有管理 UI，也没有持久化。
 * 分组的增删改与「主机归组」交互归后续迭代，可与后端 `hosts.group_id` 列一起落地。
 */
export interface HostGroup {
  /** 稳定标识，如 `production` */
  id: string
  /**
   * 显示名的 i18n key，如 `host.group.production`。
   *
   * 存 key 而非字面文案，避免把中文/英文写死在 store 里；
   * 未分组主机使用约定 key `host.group.ungrouped`。
   */
  nameKey: string
  /** 描述的 i18n key；同样存 key，理由同 `nameKey` */
  descriptionKey?: string
  /** 排序权重，升序；未分组主机恒排在所有分组之后 */
  order: number
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
 * 总览页用的主机视图：在 HostInfo 基础上补充连通状态、资源计数、版本与分组。
 * 后端聚合接口（0.0.8）就绪前，仅 status / counts / version / startedAt 可能缺失。
 */
export interface HostOverview extends HostInfo {
  status?: HostStatus
  counts?: HostCounts
  /**
   * 主机启动时间（ISO 8601 字符串）。
   *
   * 用字符串而非时间戳数字，避免前端时区歧义。
   * TODO(0.0.8)：由后端 `GetInfoAsync()` 的 SystemTime 填充。
   */
  startedAt?: string
  /** Docker 版本信息；离线主机取不到时为 undefined */
  version?: HostVersion
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

/** 当前登录用户 */
export interface CurrentUser {
  id: string
  username: string
  /**
   * 角色名（后端 `roles` 表，内置值为 `admin` / `operator` / `viewer` / `auditor`）。
   *
   * 刻意声明为 `string` 而非字面量联合：角色是**数据**不是枚举，
   * 新增角色不应该需要改前端类型（0.0.8 起支持自定义角色）。
   * 判断用 `=== 'admin'`（见 auth store 的 `isAdmin`），而不是穷举所有角色。
   */
  role: string
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
 * 容器（对应后端 `Docker.Manager.Application.Containers`，0.0.9 已实现）
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
  /** 健康检查状态 `healthy` / `unhealthy` / `starting`；未配健康检查为 null */
  healthStatus: string | null
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
}

/** 容器日志（**整段文本**，不是行数组——见后端类型注释） */
export interface ContainerLogsResult {
  containerId: string
  content: string
}

/** 创建容器的结果 */
export interface ContainerCreatedResult {
  containerId: string
  /** 守护进程给的提示（如「端口未映射」）；无提示时为空数组。**不要当错误处理**。 */
  warnings: string[]
}
