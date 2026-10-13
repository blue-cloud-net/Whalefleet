import axios, { type AxiosError, type AxiosInstance, type AxiosRequestConfig } from 'axios'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { ErrorCode, type ApiError, type ApiResult } from '@/types/api'

/**
 * axios 实例 + 拦截器：
 * 1. 请求：自动注入 X-Host-Id、Authorization
 * 2. 响应：解包 ApiResult，把 data 提到外层；非 success 抛 ApiError
 * 3. 错误：401 / token 过期 → 跳登录；业务失败 → toast
 */

const TOKEN_EXPIRED_CODES: number[] = [ErrorCode.AuthTokenExpired, ErrorCode.AuthTokenRevoked]

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
  const host = useHostStore()

  // 仅在主机作用域注入 X-Host-Id。
  // 全局作用域（总览 / 主机管理 / 用户管理）currentId 为空，
  // 此时若仍带 X-Host-Id 会让后端误判请求是针对某台机器的资源操作。
  if (host.currentId) {
    config.headers.set('X-Host-Id', host.currentId)
  }

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

async function handleHttpError(error: unknown): Promise<never> {
  if (axios.isAxiosError(error)) {
    const body = error.response?.data as ApiResult | undefined
    const apiErr: ApiError = body
      ? { code: body.code, message: body.message }
      : {
          code: error.response?.status === 401 ? ErrorCode.Unauthorized : ErrorCode.Internal,
          message: error.message
        }

    // 401 / token 过期 → 清登录态 + 跳登录
    if (error.response?.status === 401 || TOKEN_EXPIRED_CODES.includes(apiErr.code)) {
      const auth = useAuthStore()
      auth.logout()
      const redirect = encodeURIComponent(window.location.pathname + window.location.search)
      window.location.replace(`/login?redirect=${redirect}`)
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