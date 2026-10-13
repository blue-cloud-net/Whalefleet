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

/** 业务错误分段（与后端 ErrorCodeCatalog 对齐） */
export const ErrorCode = {
  // 通用（1xxx）
  Validation: 1000,
  NotFound: 1001,
  Unauthorized: 1002,
  Timeout: 1003,
  Forbidden: 1004,
  Conflict: 1005,
  // 鉴权（11xx）
  AuthInvalidCredential: 1100,
  AuthTokenExpired: 1101,
  AuthTokenRevoked: 1102,
  AuthRateLimited: 1103,
  // 主机（12xx）
  HostUnreachable: 1200,
  HostNotFound: 1201,
  // 资源（14xx）
  ResourceNotFound: 1401,
  DockerApi: 1402,
  OperationTimeout: 1403,
  // 权限（15xx）
  ResourceDenied: 1501,
  // 系统（9xxx）
  Internal: 9999
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
  role: 'admin' | 'user'
}