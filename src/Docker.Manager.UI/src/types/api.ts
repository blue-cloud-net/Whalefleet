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
  description?: string
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