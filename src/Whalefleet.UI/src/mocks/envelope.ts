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
 * 与后端 `Whalefleet.Application.Common.ErrorCodes` 逐码一致。
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
  /** 账号已停用 → 401 */
  AccountDisabled: 1108,

  /** 用户名已存在 → 400 */
  UsernameAlreadyExists: 1200,
  /** 用户不存在 → 404 */
  UserNotFound: 1201,
  /** 密码强度不满足要求 → 400 */
  WeakPassword: 1202,
  /** 团队名已占用 → 400 */
  TeamNameAlreadyExists: 1203,
  /** 团队不存在 → 404 */
  TeamNotFound: 1204,
  /** 必须先修改密码 → 403 */
  PasswordChangeRequired: 1205,

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
  HostTagNotFound: 1306,
  HostTagNameAlreadyExists: 1307,

  /** 容器 / 镜像 / Volume / Network 不存在 → 404 */
  DockerResourceNotFound: 1401,
  /** Docker API 调用失败 → 500 */
  DockerApiFailed: 1402,
  /** Docker 操作超时 → 408 */
  DockerTimeout: 1403,
  /** 资源当前状态不允许该操作 → 409（如不带 force 删除运行中的容器） */
  DockerResourceConflict: 1404,
  /** 镜像自动更新策略不存在 → 404 */
  ImageUpdatePolicyNotFound: 1411,
  /** 同一主机上已有该镜像的更新策略 → 400 */
  ImageUpdatePolicyAlreadyExists: 1412,

  /** 无权限访问此资源 → 403 */
  ResourceForbidden: 1500,
  /** 无权限访问此主机 → 403 */
  HostForbidden: 1501,
  /** 无权限执行此操作 → 403 */
  OperationForbidden: 1502,
  /** 授权记录不存在 → 404 */
  PermissionNotFound: 1503,
  /** 角色不存在 → 404 */
  RoleNotFound: 1510,
  /** 角色标识已存在 → 400 */
  RoleNameAlreadyExists: 1511,
  /** 该角色仍有有效分配，不能删除 → 400 */
  RoleInUse: 1512,
  /** 内置角色不可修改或删除 → 400 */
  RoleProtected: 1513,
  /** 未知的权限标识 → 400 */
  PermissionUnknown: 1514,
  /** 角色分配不存在 → 404 */
  RoleAssignmentNotFound: 1515,
  /** 团队角色分配不存在 → 404 */
  TeamRoleAssignmentNotFound: 1516,

  /** 仓库配置不存在 → 404 */
  RepositoryNotFound: 1800,
  /** 仓库别名已被占用 → 400 */
  RepositoryNameAlreadyExists: 1801,
  /** 仓库服务地址已被占用 → 400 */
  RepositoryAddressAlreadyExists: 1802,
  /** 未登记该地址的仓库凭据 → 400 */
  RepositoryCredentialMissing: 1803,
  /** 仓库拒绝了该凭据 → 400 */
  RepositoryAuthFailed: 1804,
  /** 仓库不可达 → 502 */
  RepositoryUnreachable: 1805,
  /** 镜像构建失败 → 500 */
  ImageBuildFailed: 1806,
  /** 镜像推送失败 → 500 */
  ImagePushFailed: 1807,

  /** Compose 项目不存在 → 404 */
  ComposeNotFound: 1900,
  /** Compose 项目部署 / 更新失败 → 500 */
  ComposeDeployFailed: 1901,
  /** Compose 项目名称已被占用 → 400 */
  ComposeNameAlreadyExists: 1902,
  /** 该 Compose 项目不是本平台部署的，无法更新 → 409 */
  ComposeNotManaged: 1903,
  /** Compose 文件非法或含暂不支持的字段 → 400 */
  ComposeInvalid: 1904,
  /** 该 Compose 项目已有进行中的操作 → 409 */
  ComposeOperationInProgress: 1905,
  /** Webhook 令牌无效或已吊销 → 404 */
  ComposeWebhookTokenInvalid: 1906,
  /** Webhook 触发过于频繁 → 429 */
  ComposeWebhookRateLimited: 1907,
  /** 变量未定义（YAML 引用了不存在的变量）→ 400 */
  VariableNotDefined: 1908,
  /** 变量名非法 → 400 */
  InvalidVariableName: 1909,
  /** 该变量不允许被 Webhook 覆盖 → 400 */
  VariableNotOverridable: 1910,
  /** 容器 Webhook 令牌无效或已吊销 → 404 */
  ContainerWebhookTokenInvalid: 1911,
  /** 容器 Webhook 触发过于频繁 → 429 */
  ContainerWebhookRateLimited: 1912,
  /** 变量不存在 → 404 */
  VariableNotFound: 1913,
  /** 变量名已被占用 → 400 */
  VariableNameAlreadyExists: 1914,
  /** 容器 Webhook 不存在 → 404 */
  ContainerWebhookNotFound: 1915,

  /** 告警规则不存在 → 404 */
  AlertRuleNotFound: 1700,
  /** 告警规则参数非法 → 400 */
  AlertRuleInvalid: 1701,
  /** 通知渠道不存在 → 404 */
  NotificationChannelNotFound: 1702,
  /** 通知渠道测试失败 → 502 */
  NotificationChannelTestFailed: 1703,
  /** 通知不存在 → 404 */
  NotificationNotFound: 1704,
  /** 通知渠道名已被占用 → 400 */
  NotificationChannelNameAlreadyExists: 1705,

  // ===== 1405-1410 文件传输（0.14.0）=====
  /** 文件路径非法 → 400 */
  InvalidFilePath: 1405,
  /** 文件或目录不存在 → 404 */
  FilePathNotFound: 1406,
  /** 上传体积超过上限 → 413 */
  UploadTooLarge: 1407,
  /** 下载票据无效或已过期 → 410 */
  DownloadTicketInvalid: 1408,
  /** 文件写入已被管理员禁用 → 403 */
  FileWriteDisabled: 1409,
  /** 导出任务不存在 → 404 */
  TransferJobNotFound: 1410,

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
  [ErrorCode.AccountDisabled]: 401,

  [ErrorCode.UsernameAlreadyExists]: 400,
  [ErrorCode.UserNotFound]: 404,
  [ErrorCode.WeakPassword]: 400,
  [ErrorCode.TeamNameAlreadyExists]: 400,
  [ErrorCode.TeamNotFound]: 404,
  [ErrorCode.PasswordChangeRequired]: 403,

  [ErrorCode.HostNotFound]: 404,
  [ErrorCode.HostUnreachable]: 502,
  [ErrorCode.HostDisabled]: 400,
  [ErrorCode.HostNameAlreadyExists]: 400,
  [ErrorCode.GroupNameAlreadyExists]: 400,
  [ErrorCode.GroupNotFound]: 404,
  [ErrorCode.HostTagNotFound]: 404,
  [ErrorCode.HostTagNameAlreadyExists]: 400,

  [ErrorCode.DockerResourceNotFound]: 404,
  [ErrorCode.DockerApiFailed]: 500,
  [ErrorCode.DockerTimeout]: 408,
  [ErrorCode.DockerResourceConflict]: 409,
  [ErrorCode.ImageUpdatePolicyNotFound]: 404,
  [ErrorCode.ImageUpdatePolicyAlreadyExists]: 400,

  [ErrorCode.ResourceForbidden]: 403,
  [ErrorCode.HostForbidden]: 403,
  [ErrorCode.OperationForbidden]: 403,
  [ErrorCode.PermissionNotFound]: 404,

  [ErrorCode.RoleNotFound]: 404,
  [ErrorCode.RoleNameAlreadyExists]: 400,
  [ErrorCode.RoleInUse]: 400,
  [ErrorCode.RoleProtected]: 400,
  [ErrorCode.PermissionUnknown]: 400,
  [ErrorCode.RoleAssignmentNotFound]: 404,
  [ErrorCode.TeamRoleAssignmentNotFound]: 404,

  [ErrorCode.RepositoryNotFound]: 404,
  [ErrorCode.RepositoryNameAlreadyExists]: 400,
  [ErrorCode.RepositoryAddressAlreadyExists]: 400,
  [ErrorCode.RepositoryCredentialMissing]: 400,
  [ErrorCode.RepositoryAuthFailed]: 400,
  [ErrorCode.RepositoryUnreachable]: 502,
  [ErrorCode.ImageBuildFailed]: 500,
  [ErrorCode.ImagePushFailed]: 500,

  [ErrorCode.ComposeNotFound]: 404,
  [ErrorCode.ComposeDeployFailed]: 500,
  [ErrorCode.ComposeNameAlreadyExists]: 400,
  [ErrorCode.ComposeNotManaged]: 409,
  [ErrorCode.ComposeInvalid]: 400,
  [ErrorCode.ComposeOperationInProgress]: 409,
  [ErrorCode.ComposeWebhookTokenInvalid]: 404,
  [ErrorCode.ComposeWebhookRateLimited]: 429,
  [ErrorCode.VariableNotDefined]: 400,
  [ErrorCode.InvalidVariableName]: 400,
  [ErrorCode.VariableNotOverridable]: 400,
  [ErrorCode.ContainerWebhookTokenInvalid]: 404,
  [ErrorCode.ContainerWebhookRateLimited]: 429,
  [ErrorCode.VariableNotFound]: 404,
  [ErrorCode.VariableNameAlreadyExists]: 400,
  [ErrorCode.ContainerWebhookNotFound]: 404,

  [ErrorCode.AlertRuleNotFound]: 404,
  [ErrorCode.AlertRuleInvalid]: 400,
  [ErrorCode.NotificationChannelNotFound]: 404,
  [ErrorCode.NotificationChannelTestFailed]: 502,
  [ErrorCode.NotificationNotFound]: 404,
  [ErrorCode.NotificationChannelNameAlreadyExists]: 400,

  [ErrorCode.InvalidFilePath]: 400,
  [ErrorCode.FilePathNotFound]: 404,
  [ErrorCode.UploadTooLarge]: 413,
  [ErrorCode.DownloadTicketInvalid]: 410,
  [ErrorCode.FileWriteDisabled]: 403,
  [ErrorCode.TransferJobNotFound]: 404,

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
  [ErrorCode.AccountDisabled]: '账号已停用',

  [ErrorCode.UsernameAlreadyExists]: '用户名已存在',
  [ErrorCode.UserNotFound]: '用户不存在',
  [ErrorCode.WeakPassword]: '密码强度不满足要求',
  [ErrorCode.TeamNameAlreadyExists]: '团队名已存在',
  [ErrorCode.TeamNotFound]: '团队不存在',
  [ErrorCode.PasswordChangeRequired]: '必须先修改密码',

  [ErrorCode.HostNotFound]: '主机不存在',
  [ErrorCode.HostUnreachable]: '主机连通性失败',
  [ErrorCode.HostDisabled]: '主机已停用',
  [ErrorCode.HostNameAlreadyExists]: '主机名已被占用',
  [ErrorCode.GroupNameAlreadyExists]: '分组名已被占用',
  [ErrorCode.GroupNotFound]: '分组不存在',
  [ErrorCode.HostTagNotFound]: '主机标签不存在',
  [ErrorCode.HostTagNameAlreadyExists]: '主机标签名已被占用',

  [ErrorCode.DockerResourceNotFound]: '容器 / 镜像 / Volume / Network 不存在',
  [ErrorCode.DockerApiFailed]: 'Docker API 调用失败',
  [ErrorCode.DockerTimeout]: 'Docker 操作超时',
  [ErrorCode.DockerResourceConflict]: '资源当前状态不允许该操作',

  [ErrorCode.ResourceForbidden]: '无权限访问此资源',
  [ErrorCode.HostForbidden]: '无权限访问此主机',
  [ErrorCode.OperationForbidden]: '无权限执行此操作',
  [ErrorCode.PermissionNotFound]: '授权不存在',

  [ErrorCode.RoleNotFound]: '角色不存在',
  [ErrorCode.RoleNameAlreadyExists]: '角色标识已存在',
  [ErrorCode.RoleInUse]: '该角色仍有有效分配，请先撤销分配',
  [ErrorCode.RoleProtected]: '内置角色不可修改或删除',
  [ErrorCode.PermissionUnknown]: '未知的权限标识',
  [ErrorCode.RoleAssignmentNotFound]: '角色分配不存在',
  [ErrorCode.TeamRoleAssignmentNotFound]: '团队角色分配不存在',

  [ErrorCode.RepositoryNotFound]: '仓库配置不存在',
  [ErrorCode.RepositoryNameAlreadyExists]: '仓库别名已被占用',
  [ErrorCode.RepositoryAddressAlreadyExists]: '仓库地址已被占用',
  [ErrorCode.RepositoryCredentialMissing]: '未登记该地址的仓库凭据',
  [ErrorCode.RepositoryAuthFailed]: '仓库拒绝了该凭据',
  [ErrorCode.RepositoryUnreachable]: '仓库不可达',
  [ErrorCode.ImageBuildFailed]: '镜像构建失败',
  [ErrorCode.ImagePushFailed]: '镜像推送失败',

  [ErrorCode.ComposeNotFound]: 'Compose 项目不存在',
  [ErrorCode.ComposeDeployFailed]: 'Compose 项目部署 / 更新失败',
  [ErrorCode.ComposeNameAlreadyExists]: 'Compose 项目名称已被占用',
  [ErrorCode.ComposeNotManaged]: '该 Compose 项目不是本平台部署的，无法更新',
  [ErrorCode.ComposeInvalid]: 'Compose 文件非法或含暂不支持的字段',
  [ErrorCode.ComposeOperationInProgress]: '该 Compose 项目已有进行中的操作',
  [ErrorCode.ComposeWebhookTokenInvalid]: 'Webhook 令牌无效或已吊销',
  [ErrorCode.ComposeWebhookRateLimited]: 'Webhook 触发过于频繁',
  [ErrorCode.VariableNotDefined]: '变量未定义',
  [ErrorCode.InvalidVariableName]: '变量名非法',
  [ErrorCode.VariableNotOverridable]: '该变量不允许被 Webhook 覆盖',
  [ErrorCode.ContainerWebhookTokenInvalid]: '容器 Webhook 令牌无效或已吊销',
  [ErrorCode.ContainerWebhookRateLimited]: '容器 Webhook 触发过于频繁',
  [ErrorCode.VariableNotFound]: '变量不存在',
  [ErrorCode.VariableNameAlreadyExists]: '变量名已被占用',
  [ErrorCode.ContainerWebhookNotFound]: '容器 Webhook 不存在',
  [ErrorCode.ImageUpdatePolicyNotFound]: '镜像自动更新策略不存在',
  [ErrorCode.ImageUpdatePolicyAlreadyExists]: '该镜像已有更新策略',

  [ErrorCode.AlertRuleNotFound]: '告警规则不存在',
  [ErrorCode.AlertRuleInvalid]: '告警规则参数非法',
  [ErrorCode.NotificationChannelNotFound]: '通知渠道不存在',
  [ErrorCode.NotificationChannelTestFailed]: '通知渠道测试失败',
  [ErrorCode.NotificationNotFound]: '通知不存在',
  [ErrorCode.NotificationChannelNameAlreadyExists]: '通知渠道名已被占用',

  [ErrorCode.InvalidFilePath]: '文件路径非法',
  [ErrorCode.FilePathNotFound]: '文件或目录不存在',
  [ErrorCode.UploadTooLarge]: '上传体积超过上限',
  [ErrorCode.DownloadTicketInvalid]: '下载票据无效或已过期',
  [ErrorCode.FileWriteDisabled]: '文件写入已被管理员禁用',
  [ErrorCode.TransferJobNotFound]: '导出任务不存在',

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