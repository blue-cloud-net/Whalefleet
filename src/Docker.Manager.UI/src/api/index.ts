import axios, {
  type AxiosError,
  type AxiosInstance,
  type AxiosRequestConfig,
  type AxiosResponse,
  type InternalAxiosRequestConfig
} from 'axios'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import { ErrorCode, type ApiError, type ApiResult, type LoginResult } from '@/types/api'

/**
 * axios 实例 + 拦截器：
 * 1. 请求：注入 Authorization（`hostId` 走 URL 路径，不经请求头）
 * 2. 响应：解包 ApiResult，把 data 提到外层；非 success 抛 ApiRequestError
 * 3. 错误：401 → 静默刷新一次并重放原请求；刷不动才清会话跳登录。其余业务失败 → toast
 *
 * **`hostId` 为什么不在请求头**：`X-Host-Id` 已废弃（见 docs/api-spec.md 的「请求风格约定」），
 * 主机标识只出现在路径里（`/api/v1/hosts/{hostId}/...`）。因此本文件不再需要主机 store。
 */

/** 需要重新登录的响应码：401 本身，以及两个令牌失效码 */
const TOKEN_EXPIRED_CODES: number[] = [ErrorCode.TokenExpired, ErrorCode.RefreshTokenRevoked]

/**
 * 认证端点：这些请求自身的 401 **不触发**刷新重放。
 *
 * 否则「登录密码错误」会变成「先刷一次再报错」，
 * 而「刷新令牌失效」会自己套自己。
 */
const AUTH_ENDPOINT_PATHS = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout']

const LOGIN_PATH = '/login'

/** 带重试标记的请求配置：axios 没有这个字段，由本层自己挂。 */
interface RetriableConfig extends InternalAxiosRequestConfig {
  _retried?: boolean
}

export const http: AxiosInstance = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/',
  timeout: 30_000,
  headers: {
    'Content-Type': 'application/json'
  }
})

// ===== 请求拦截器 =====
http.interceptors.request.use((config) => {
  const auth = useAuthStore()

  if (auth.accessToken) {
    config.headers.set('Authorization', `Bearer ${auth.accessToken}`)
  }

  return config
})

// ===== 响应拦截器 =====
http.interceptors.response.use(
  (response) => {
    const body = response.data as ApiResult
    if (body && typeof body === 'object' && 'success' in body) {
      if (body.success) {
        // 解包：data 提到外层
        response.data = body.data
        return response
      }
      // 业务失败
      throw new ApiRequestError({
        code: body.code,
        message: body.message || 'Unknown error'
      })
    }
    // 非 ApiResult 响应（/healthz 等）原样返回
    return response
  },
  async (error: AxiosError<ApiResult>) => {
    return handleHttpError(error)
  }
)

/**
 * 错误统一收口。
 *
 * 返回 `AxiosResponse` 而不只是抛异常：401 静默刷新成功后要**重放原请求**，
 * 那条成功响应就是本函数的返回值。
 */
async function handleHttpError(error: unknown): Promise<AxiosResponse> {
  if (axios.isAxiosError(error)) {
    const body = error.response?.data as ApiResult | undefined
    const apiErr: ApiError = body
      ? { code: body.code, message: body.message }
      : {
          code: error.response?.status === 401 ? ErrorCode.Unauthenticated : ErrorCode.InternalError,
          message: error.message
        }

    if (isUnauthorized(error, apiErr)) {
      const config = error.config as RetriableConfig | undefined

      // 认证端点自身的 401：既不重放也不跳转，交给调用方（登录页 / 刷新流程）决定。
      // 这里跳转会把「密码输错」表现成「被登出」，属于语义错位。
      if (!config || isAuthEndpoint(config.url)) {
        throw new ApiRequestError(apiErr)
      }

      // 只重放一次：避免「刷新成功但该接口仍返回 401」时无限循环。
      if (!config._retried) {
        const token = await refreshAccessToken()

        if (token) {
          config._retried = true
          config.headers.set('Authorization', `Bearer ${token}`)

          return http.request(config)
        }
      }

      // 续期不可用 → 会话真的结束了
      endSession()
      throw new ApiRequestError(apiErr)
    }

    if (body?.code != null) {
      ElMessage({ type: 'error', message: apiErr.message || `Error ${apiErr.code}`, showClose: true })
    }
    throw new ApiRequestError(apiErr)
  }

  if (error instanceof ApiRequestError) {
    if (!TOKEN_EXPIRED_CODES.includes(error.code)) {
      ElMessage({ type: 'error', message: error.message, showClose: true })
    }
    throw error
  }

  ElMessage({ type: 'error', message: String(error), showClose: true })
  throw error
}

/** 是否属于「需要重新登录」的失败。 */
function isUnauthorized(error: AxiosError, apiErr: ApiError): boolean {
  return error.response?.status === 401 || TOKEN_EXPIRED_CODES.includes(apiErr.code)
}

function isAuthEndpoint(url?: string): boolean {
  return url != null && AUTH_ENDPOINT_PATHS.some((path) => url.includes(path))
}

/** 进行中的刷新请求；并发 401 复用同一个 Promise。 */
let refreshInFlight: Promise<string | null> | null = null

/**
 * 静默续期 access token；失败返回 `null`（不抛，由调用方决定跳转）。
 *
 * **为什么必须做并发去重**：刷新令牌是一次性的（轮换 + 重放检测）。
 * 页面同时发出多个请求时，若每个 401 各刷一次，第二个刷新会被服务端判定为重放，
 * 进而**撤销该用户全部会话**——用户会莫名其妙被踢下线。
 */
function refreshAccessToken(): Promise<string | null> {
  refreshInFlight ??= performRefresh().finally(() => {
    refreshInFlight = null
  })

  return refreshInFlight
}

/**
 * 真正执行刷新。
 *
 * 直接走 `http` 而不是 `request()`：这条请求仍会经过响应拦截器，
 * 但它的 401 被 `isAuthEndpoint` 挡在重放之外，因此不会递归。
 */
async function performRefresh(): Promise<string | null> {
  const auth = useAuthStore()

  if (!auth.refreshToken) {
    return null
  }

  try {
    const response = await http.post<LoginResult>('/api/v1/auth/refresh', {
      refreshToken: auth.refreshToken
    })

    // 必须整体覆盖：服务端已轮换刷新令牌，沿用旧值下次就是重放。
    auth.setSession(response.data)

    return response.data.accessToken
  } catch {
    auth.clearSession()

    return null
  }
}

/**
 * 结束会话：清本地状态并整页跳登录。
 *
 * 用整页跳转（而非 `router.push`）是为了让内存里的 store 一并重置——
 * 令牌失效时，缓存的主机与资源数据都已经不可信。
 */
function endSession(): void {
  useAuthStore().clearSession()

  if (window.location.pathname === LOGIN_PATH) {
    return
  }

  const redirect = encodeURIComponent(window.location.pathname + window.location.search)

  window.location.replace(`${LOGIN_PATH}?redirect=${redirect}`)
}

/**
 * 业务错误：携带后端 ApiError 字段。
 */
export class ApiRequestError extends Error {
  readonly code: number
  readonly traceId?: string
  readonly details?: Record<string, unknown>

  constructor(err: ApiError) {
    super(err.message)
    this.name = 'ApiRequestError'
    this.code = err.code
    this.traceId = err.traceId
    this.details = err.details
  }
}

/**
 * 类型友好的请求包装：自动推断 data 类型。
 */
export async function request<T = unknown>(config: AxiosRequestConfig): Promise<T> {
  const res = await http.request<T>(config)
  return res.data
}