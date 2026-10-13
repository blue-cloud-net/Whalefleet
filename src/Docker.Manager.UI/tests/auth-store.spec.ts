import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { login, logoutSession, refreshSession } from '@/api/auth'
import { useAuthStore } from '@/stores/auth'
import type { LoginResult } from '@/types/api'

vi.mock('@/api/auth', () => ({
  login: vi.fn(),
  refreshSession: vi.fn(),
  logoutSession: vi.fn()
}))

const mockedLogin = vi.mocked(login)
const mockedRefresh = vi.mocked(refreshSession)
const mockedLogout = vi.mocked(logoutSession)

function session(overrides: Partial<LoginResult> = {}): LoginResult {
  return {
    accessToken: 'access-1',
    refreshToken: 'refresh-1',
    expiresIn: 900,
    user: { id: 'u-1', username: 'admin', role: 'admin' },
    ...overrides
  }
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
})

describe('认证 store — 会话落库', () => {
  it('setSession 同时写入两个令牌与用户', () => {
    const auth = useAuthStore()

    auth.setSession(session())

    expect(auth.accessToken).toBe('access-1')
    expect(auth.refreshToken).toBe('refresh-1')
    expect(auth.user?.username).toBe('admin')
    expect(auth.isAuthenticated).toBe(true)
  })

  it('isAdmin 只认 admin：自定义角色不应被当成管理员', () => {
    const auth = useAuthStore()

    auth.setSession(session({ user: { id: 'u-2', username: 'op', role: 'operator' } }))

    expect(auth.isAuthenticated).toBe(true)
    expect(auth.isAdmin).toBe(false)
  })

  it('clearSession 清空令牌与用户', () => {
    const auth = useAuthStore()
    auth.setSession(session())

    auth.clearSession()

    expect(auth.accessToken).toBe('')
    expect(auth.refreshToken).toBe('')
    expect(auth.user).toBeNull()
    expect(auth.isAuthenticated).toBe(false)
  })
})

describe('认证 store — 登录', () => {
  it('成功后写入会话', async () => {
    mockedLogin.mockResolvedValue(session())
    const auth = useAuthStore()

    await auth.login('admin', 'StrongP@ss123')

    expect(mockedLogin).toHaveBeenCalledWith({ username: 'admin', password: 'StrongP@ss123' })
    expect(auth.accessToken).toBe('access-1')
  })

  it('失败时抛出且不写入任何会话（由登录页决定文案）', async () => {
    mockedLogin.mockRejectedValue(new Error('凭据错误'))
    const auth = useAuthStore()

    await expect(auth.login('admin', 'wrong')).rejects.toThrow('凭据错误')

    expect(auth.accessToken).toBe('')
    expect(auth.user).toBeNull()
  })
})

describe('认证 store — 刷新', () => {
  it('必须覆盖刷新令牌本身（轮换语义）', async () => {
    mockedRefresh.mockResolvedValue(session({ accessToken: 'access-2', refreshToken: 'refresh-2' }))
    const auth = useAuthStore()
    auth.setSession(session())

    await expect(auth.refresh()).resolves.toBe('access-2')

    // 沿用旧刷新令牌，下一次会被服务端判定为重放并撤销全部会话。
    expect(auth.refreshToken).toBe('refresh-2')
    expect(mockedRefresh).toHaveBeenCalledWith('refresh-1')
  })
})

describe('认证 store — 登出', () => {
  it('把旧刷新令牌送去撤销，并清空本地会话', async () => {
    mockedLogout.mockResolvedValue(undefined)
    const auth = useAuthStore()
    auth.setSession(session())

    await auth.logout()

    expect(mockedLogout).toHaveBeenCalledWith('refresh-1')
    expect(auth.accessToken).toBe('')
    expect(auth.user).toBeNull()
  })

  it('服务端撤销失败也不影响本地已登出', async () => {
    mockedLogout.mockRejectedValue(new Error('network down'))
    const auth = useAuthStore()
    auth.setSession(session())

    await expect(auth.logout()).resolves.toBeUndefined()

    expect(auth.isAuthenticated).toBe(false)
  })

  it('无刷新令牌时不发请求（纯本地登出）', async () => {
    const auth = useAuthStore()

    await auth.logout()

    expect(mockedLogout).not.toHaveBeenCalled()
  })
})
