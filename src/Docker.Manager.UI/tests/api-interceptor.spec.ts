import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios'
import { ApiRequestError, http } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { ErrorCode, type ApiResult, type LoginResult } from '@/types/api'

/**
 * HTTP 层拦截器测试。
 *
 * 守的是 0.0.5 最容易写错、且错了很难查的一段逻辑：
 * 401 → 静默刷新一次 → 重放原请求。刷新令牌是**一次性**的，
 * 并发场景下多刷一次就会被服务端判为重放、撤销该用户全部会话。
 */

interface Stub {
  status: number
  body: unknown
}

/** 记录每次真正发出的请求 URL，用于断言"刷新只发生了一次"。 */
let requestedUrls: string[] = []

/** 每个用例自行替换的应答逻辑；允许 async（用于制造"刷新在途"的并发场景）。 */
let respond: (config: InternalAxiosRequestConfig) => Stub | Promise<Stub>

/** 会话真的结束时会被调用的整页跳转；默认替换掉，避免 jsdom 报 navigation 未实现。 */
const replace = vi.fn()

function envelope<T>(data: T): ApiResult<T> {
  return { success: true, code: ErrorCode.Success, message: 'ok', data }
}

function failure(code: number, message: string): ApiResult<null> {
  return { success: false, code, message, data: null }
}

function tokenPair(accessToken: string, refreshToken: string): LoginResult {
  return {
    accessToken,
    refreshToken,
    expiresIn: 900,
    user: { id: 'u-1', username: 'admin', role: 'admin' }
  }
}

function session(): LoginResult {
  return tokenPair('access-1', 'refresh-1')
}

/** 把 stub 的结果转成 axios 期望的响应 / 异常。 */
const stubAdapter: AxiosAdapter = async (config) => {
  requestedUrls.push(String(config.url))

  const stub = await respond(config)
  const response = {
    data: stub.body,
    status: stub.status,
    statusText: '',
    headers: {},
    config
  }

  if (stub.status >= 400) {
    throw new AxiosError('Request failed', undefined, config, undefined, response)
  }

  return response
}

beforeEach(() => {
  setActivePinia(createPinia())
  requestedUrls = []
  replace.mockClear()
  respond = () => ({ status: 200, body: envelope(null) })
  http.defaults.adapter = stubAdapter

  // jsdom 未实现真实导航：替换 location 以便断言跳转，并避免噪声。
  Object.defineProperty(window, 'location', {
    configurable: true,
    value: { pathname: '/hosts/local/containers', search: '', replace }
  })
})

describe('HTTP 拦截器 — 请求头', () => {
  it('注入 Authorization', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    let authorization: string | null = null
    respond = (config) => {
      authorization = config.headers.get('Authorization') as string | null

      return { status: 200, body: envelope(null) }
    }

    await http.get('/api/v1/auth/me')

    expect(authorization).toBe('Bearer access-1')
  })

  it('不再注入已废弃的 X-Host-Id（主机标识只走 URL 路径）', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    let hostHeader: unknown = 'unset'
    respond = (config) => {
      hostHeader = config.headers.get('X-Host-Id')

      return { status: 200, body: envelope(null) }
    }

    await http.get('/api/v1/hosts/local/containers')

    expect(hostHeader).toBeUndefined()
  })
})

describe('HTTP 拦截器 — 响应解包', () => {
  it('成功时把 data 提到外层', async () => {
    respond = () => ({ status: 200, body: envelope({ name: 'local' }) })

    const response = await http.get('/api/v1/hosts')

    expect(response.data).toEqual({ name: 'local' })
  })

  it('2xx 但 success=false 时抛业务错误', async () => {
    respond = () => ({ status: 200, body: failure(ErrorCode.HostNotFound, '主机不存在') })

    await expect(http.get('/api/v1/hosts/nope')).rejects.toBeInstanceOf(ApiRequestError)
  })
})

describe('HTTP 拦截器 — 401 静默刷新', () => {
  it('刷新一次后重放原请求，并整体轮换两个令牌', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    respond = (config) => {
      if (config.url?.includes('/api/v1/auth/refresh')) {
        return { status: 200, body: envelope(tokenPair('access-2', 'refresh-2')) }
      }

      return config.headers.get('Authorization') === 'Bearer access-2'
        ? { status: 200, body: envelope({ name: 'local' }) }
        : { status: 401, body: failure(ErrorCode.TokenExpired, '令牌已过期') }
    }

    const response = await http.get('/api/v1/hosts')

    expect(response.data).toEqual({ name: 'local' })
    expect(requestedUrls.filter((url) => url.includes('/auth/refresh'))).toHaveLength(1)
    expect(auth.accessToken).toBe('access-2')
    expect(auth.refreshToken).toBe('refresh-2')
    expect(replace).not.toHaveBeenCalled()
  })

  it('并发 401 只刷新一次（多刷一次会被判为重放）', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    respond = async (config) => {
      if (config.url?.includes('/api/v1/auth/refresh')) {
        // 让刷新“在途”，使第二个请求的 401 落在刷新期间
        await new Promise((resolve) => setTimeout(resolve, 5))

        return { status: 200, body: envelope(tokenPair('access-2', 'refresh-2')) }
      }

      return config.headers.get('Authorization') === 'Bearer access-2'
        ? { status: 200, body: envelope({ name: 'local' }) }
        : { status: 401, body: failure(ErrorCode.TokenExpired, '令牌已过期') }
    }

    const results = await Promise.all([
      http.get('/api/v1/hosts'),
      http.get('/api/v1/hosts')
    ])

    expect(requestedUrls.filter((url) => url.includes('/auth/refresh'))).toHaveLength(1)
    expect(results.map((response) => response.data)).toEqual([
      { name: 'local' },
      { name: 'local' }
    ])
    expect(auth.refreshToken).toBe('refresh-2')
  })

  it('刷新不可用时清会话并跳登录，且不无限重试', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    respond = (config) =>
      config.url?.includes('/api/v1/auth/refresh')
        ? { status: 401, body: failure(ErrorCode.RefreshTokenRevoked, '刷新令牌无效') }
        : { status: 401, body: failure(ErrorCode.TokenExpired, '令牌已过期') }

    await expect(http.get('/api/v1/hosts')).rejects.toBeInstanceOf(ApiRequestError)

    expect(requestedUrls.filter((url) => url.includes('/auth/refresh'))).toHaveLength(1)
    expect(auth.refreshToken).toBe('')
    expect(replace).toHaveBeenCalledWith('/login?redirect=%2Fhosts%2Flocal%2Fcontainers')
  })

  it('重放后仍 401 时不再刷新第二次（防死循环）', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    respond = (config) =>
      config.url?.includes('/api/v1/auth/refresh')
        ? { status: 200, body: envelope(tokenPair('access-2', 'refresh-2')) }
        : { status: 401, body: failure(ErrorCode.TokenExpired, '令牌已过期') }

    await expect(http.get('/api/v1/hosts')).rejects.toBeInstanceOf(ApiRequestError)

    expect(requestedUrls.filter((url) => url.includes('/auth/refresh'))).toHaveLength(1)
    expect(replace).toHaveBeenCalled()
  })
})

describe('HTTP 拦截器 — 认证端点自身的 401', () => {
  it('登录失败不刷新也不跳登录页（否则会把"密码错"表现成"被登出"）', async () => {
    respond = () => ({
      status: 401,
      body: failure(ErrorCode.Unauthenticated, '用户名或密码错误')
    })

    await expect(
      http.post('/api/v1/auth/login', { username: 'admin', password: 'wrong' })
    ).rejects.toBeInstanceOf(ApiRequestError)

    expect(requestedUrls).toEqual(['/api/v1/auth/login'])
    expect(replace).not.toHaveBeenCalled()
  })

  it('刷新端点的 401 不再触发刷新（避免自噬）', async () => {
    const auth = useAuthStore()
    auth.setSession(session())

    respond = () => ({
      status: 401,
      body: failure(ErrorCode.RefreshTokenRevoked, '刷新令牌无效')
    })

    await expect(http.post('/api/v1/auth/refresh', { refreshToken: 'refresh-1' })).rejects.toBeInstanceOf(
      ApiRequestError
    )

    expect(requestedUrls).toEqual(['/api/v1/auth/refresh'])
    expect(replace).not.toHaveBeenCalled()
  })
})
