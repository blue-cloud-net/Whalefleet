import { request } from '@/api'
import type { LoginResult } from '@/types/api'

/**
 * 认证端点（**全局作用域**，路径不含 `hostId`）。
 *
 * 路径与请求/响应结构必须与 `docs/api-spec.md` 的 `POST /api/v1/auth/*` 一致；
 * 后端失败路径统一走 `ApiResult` + 全局异常中间件，因此这里不写任何
 * `try/catch` —— 错误码到 UI 文案的映射只在一处（调用方）。
 */

/**
 * 用户名 / 密码登录。
 *
 * 失败形态：`1000`/400（缺字段）、`1100`/401（凭据错误，**两种情况响应一致**）、
 * `1103`/429（失败次数超限）。
 */
export function login(payload: { username: string; password: string }): Promise<LoginResult> {
  return request<LoginResult>({
    url: '/api/v1/auth/login',
    method: 'post',
    data: payload
  })
}

/**
 * 用刷新令牌换取新的令牌对（**轮换**）。
 *
 * 服务端在签发新令牌的同时立刻作废旧刷新令牌，因此调用方**必须**用返回值覆盖本地
 * 存储的两个令牌。沿用旧值再刷一次会被判定为重放，进而撤销该用户全部会话。
 *
 * 失败形态：`1000`/400（未提供）、`1102`/401（无效 / 已撤销 / 已过期）。
 */
export function refreshSession(refreshToken: string): Promise<LoginResult> {
  return request<LoginResult>({
    url: '/api/v1/auth/refresh',
    method: 'post',
    data: { refreshToken }
  })
}

/**
 * 登出（**幂等**）。
 *
 * 令牌不存在或已撤销同样返回成功，因此调用方无需区分"首次登出"与"重复登出"。
 * 响应无 `data` 字段，是 `ApiResult` 而非 `ApiResult<T>`。
 */
export function logoutSession(refreshToken: string): Promise<void> {
  return request<void>({
    url: '/api/v1/auth/logout',
    method: 'post',
    data: { refreshToken }
  })
}
