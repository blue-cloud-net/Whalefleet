/**
 * mock 响应的信封与错误码工具。
 *
 * ## 这是后端契约的下游副本，改动必须同步
 *
 * 本文件是 `docs/api-spec.md` 错误码表的**第五处副本**（另四处为
 * 后端 `ErrorCodes`、后端 `ErrorCodeCatalog`、后端 `ErrorHttpStatus`、前端 `types/api.ts`）。
 * 本轮决定采用「手写 mock + 守卫测试」而非先生成，因此契约漂移靠
 * `tests/mock-contract.spec.ts` 强制暴露——新增错误码却忘了改这里，测试会挂。
 *
 * 映射**逐条抄自后端真实实现**（`ErrorCodeCatalog.Classify` → `ErrorHttpStatus.ToHttpStatus`），
 * 不是抄 `docs/api-spec.md` 的表格：文档可能滞后，代码不会。
 */

/** 后端 `ApiResult` 的成功码。 */
export const CODE_SUCCESS = 0

/**
 * 业务错误码。
 *
 * 与后端 `Docker.Manager.Application.Common.ErrorCodes` 逐码一致。
 * 新增时同步四处 + 本文件，并补 `tests/mock-contract.spec.ts` 的期望数组。
 */
export const ErrorCode = {
  /** 参数校验失败 → 400 */
  ValidationFailed: 1000,
  /** 资源不存在 → 404 */
  NotFound: 1001,
  /** 通用 Docker 操作失败 → 500 */
  DockerOperationFailed: 1002,
  /** 操作超时 → 408 */
  OperationTimeout: 1003,

  /** 未认证（Token 缺失或无效）→ 401 */
  Unauthenticated: 1100,
  /** Token 已过期 → 401 */
  TokenExpired: 1101,
  /** Refresh token 已撤销或无效 → 401 */
  RefreshTokenRevoked: 1102,
  /** 登录速率限制 → 429 */
  LoginRateLimited: 1103,
  /** Terminal token 无效或已过期 → 401 */
  TerminalTokenInvalid: 1104,

  /** 用户名已存在 → 400 */
  UsernameAlreadyExists: 1200,
  /** 用户不存在 → 404 */
  UserNotFound: 1201,
  /** 密码强度不满足要求 → 400 */
  WeakPassword: 1202,

  /** 主机不存在 → 404 */
  HostNotFound: 1300,
  /** 主机连通性失败 → 502 */
  HostUnreachable: 1301,
  /** 主机已停用 → 400 */
  HostDisabled: 1302,
  /** 主机名已被占用 → 400 */
  HostNameAlreadyExists: 1303,
  /** 分组名已被占用 → 400 */
  GroupNameAlreadyExists: 1304,
  /** 分组不存在 → 404 */
  GroupNotFound: 1305,

  /** 容器 / 镜像 / Volume / Network 不存在 → 404 */
  DockerResourceNotFound: 1401,
  /** Docker API 调用失败 → 500 */
  DockerApiFailed: 1402,
  /** Docker 操作超时 → 408 */
  DockerTimeout: 1403,
  /** 资源当前状态不允许该操作 → 409（如不带 force 删除运行中的容器） */
  DockerResourceConflict: 1404,

  /** 无权限访问此资源 → 403 */
  ResourceForbidden: 1500,
  /** 无权限访问此主机 → 403 */
  HostForbidden: 1501,
  /** 无权限执行此操作 → 403 */
  OperationForbidden: 1502,

  /** 服务器内部错误 → 500 */
  InternalError: 9999
} as const

export type ErrorCodeValue = (typeof ErrorCode)[keyof typeof ErrorCode]

/**
 * 错误码 → HTTP 状态码。
 *
 * 抄自后端 `ErrorCodeCatalog.Classify`（语义分类）+ `ErrorHttpStatus.ToHttpStatus`（分类→状态）
 * 两级映射的**结果**。注意后端刻意**不按数值分段推断**——同段内既有 404 也有 502、429，
 * 所以这里必须逐码登记，不能用区间公式。
 */
export const ERROR_HTTP_STATUS: Record<number, number> = {
  [CODE_SUCCESS]: 200,

  [ErrorCode.ValidationFailed]: 400,
  [ErrorCode.NotFound]: 404,
  [ErrorCode.DockerOperationFailed]: 500,
  [ErrorCode.OperationTimeout]: 408,

  [ErrorCode.Unauthenticated]: 401,
  [ErrorCode.TokenExpired]: 401,
  [ErrorCode.RefreshTokenRevoked]: 401,
  [ErrorCode.LoginRateLimited]: 429,
  [ErrorCode.TerminalTokenInvalid]: 401,

  [ErrorCode.UsernameAlreadyExists]: 400,
  [ErrorCode.UserNotFound]: 404,
  [ErrorCode.WeakPassword]: 400,

  [ErrorCode.HostNotFound]: 404,
  [ErrorCode.HostUnreachable]: 502,
  [ErrorCode.HostDisabled]: 400,
  [ErrorCode.HostNameAlreadyExists]: 400,
  [ErrorCode.GroupNameAlreadyExists]: 400,
  [ErrorCode.GroupNotFound]: 404,

  [ErrorCode.DockerResourceNotFound]: 404,
  [ErrorCode.DockerApiFailed]: 500,
  [ErrorCode.DockerTimeout]: 408,
  [ErrorCode.DockerResourceConflict]: 409,

  [ErrorCode.ResourceForbidden]: 403,
  [ErrorCode.HostForbidden]: 403,
  [ErrorCode.OperationForbidden]: 403,

  [ErrorCode.InternalError]: 500
}

/** 错误码 → 默认文案，抄自后端 `ErrorCodeCatalog.GetDefaultMessage`。 */
export const ERROR_MESSAGES: Record<number, string> = {
  [CODE_SUCCESS]: 'ok',

  [ErrorCode.ValidationFailed]: '参数校验失败',
  [ErrorCode.NotFound]: '资源不存在',
  [ErrorCode.DockerOperationFailed]: 'Docker 操作失败',
  [ErrorCode.OperationTimeout]: '操作超时',

  [ErrorCode.Unauthenticated]: '未认证',
  [ErrorCode.TokenExpired]: 'Token 已过期',
  [ErrorCode.RefreshTokenRevoked]: 'Refresh token 已撤销或无效',
  [ErrorCode.LoginRateLimited]: '登录尝试过于频繁',
  [ErrorCode.TerminalTokenInvalid]: 'Terminal token 无效或已过期',

  [ErrorCode.UsernameAlreadyExists]: '用户名已存在',
  [ErrorCode.UserNotFound]: '用户不存在',
  [ErrorCode.WeakPassword]: '密码强度不满足要求',

  [ErrorCode.HostNotFound]: '主机不存在',
  [ErrorCode.HostUnreachable]: '主机连通性失败',
  [ErrorCode.HostDisabled]: '主机已停用',
  [ErrorCode.HostNameAlreadyExists]: '主机名已被占用',
  [ErrorCode.GroupNameAlreadyExists]: '分组名已被占用',
  [ErrorCode.GroupNotFound]: '分组不存在',

  [ErrorCode.DockerResourceNotFound]: '容器 / 镜像 / Volume / Network 不存在',
  [ErrorCode.DockerApiFailed]: 'Docker API 调用失败',
  [ErrorCode.DockerTimeout]: 'Docker 操作超时',
  [ErrorCode.DockerResourceConflict]: '资源当前状态不允许该操作',

  [ErrorCode.ResourceForbidden]: '无权限访问此资源',
  [ErrorCode.HostForbidden]: '无权限访问此主机',
  [ErrorCode.OperationForbidden]: '无权限执行此操作',

  [ErrorCode.InternalError]: '服务器内部错误'
}

/**
 * 成功响应信封。
 *
 * 后端 `ApiResult<T>` 固定四个字段，成功时 `message` 为 `"ok"`（`ErrorCodeCatalog` 的登记文案）。
 */
export function ok<T>(data: T): {
  success: true
  code: number
  message: string
  data: T
} {
  return { success: true, code: CODE_SUCCESS, message: ERROR_MESSAGES[CODE_SUCCESS], data }
}

/**
 * 失败响应信封。
 *
 * **`data` 恒为 `null` 且必须显式出现**：后端刻意不开 `WhenWritingNull`，
 * 因此失败响应体里一定有 `"data": null` 这个字段（而不是被省略）。
 * 前端拦截器会把非 success 的信封转成 `ApiRequestError`，data 不会被读到。
 */
export function fail(
  code: ErrorCodeValue,
  message?: string
): {
  success: false
  code: number
  message: string
  data: null
} {
  return {
    success: false,
    code,
    message: message ?? ERROR_MESSAGES[code] ?? '请求处理失败',
    data: null
  }
}

/** 由错误码推导 HTTP 状态码；未登记一律 500（与后端 `Classify` 的兜底一致）。 */
export function httpStatusOf(code: number): number {
  return ERROR_HTTP_STATUS[code] ?? 500
}