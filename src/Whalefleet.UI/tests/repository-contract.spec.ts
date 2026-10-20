/**
 * @vitest-environment node
 *
 * 与 `mock-contract.spec.ts` 同理由使用 **node 环境**：
 * jsdom 自带的 `AbortSignal` 与 Node 原生 `fetch` 不是同一实例，
 * MSW 的 fetch 拦截器会因此抛
 * `RequestInit: Expected signal ("AbortSignal {}") to be an instance of AbortSignal`。
 */
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { mockServer } from '../src/mocks/node'
import { setMockScenario } from '../src/mocks/index'
import { ErrorCode } from '../src/mocks/envelope'
import { resetSessionRepositories } from '../src/mocks/handlers/repositories'
import { MOCK_REPOSITORIES_DEFAULT } from '../src/mocks/data/repositories'

/**
 * 仓库配置端点的 mock 契约守卫（0.4.0）。
 *
 * ## 守的是什么
 * 这些「后端有、mock 没复刻」的语义，每一条都会让前端分支测不到：
 * 1. **响应里没有口令，也没有密文**——mock 若「好心」带一个 `password` 字段，
 *    「界面永不回显口令」这条就永远测不出来。
 * 2. **别名与地址各占一个唯一索引**（`1801` / `1802`）。地址冲突尤其重要：
 *    同址两条配置会让「推送时用哪条凭据」变成随机行为。
 * 3. **`test` 的两种失败是不同的码**：不可达 `1805` / 凭据被拒 `1804`。
 *    界面对这两者的处置完全不同（查网络 vs 重填口令）。
 * 4. **`test` 可以测尚未保存的配置**（不带 `id`）——「先试再存」动线的基础。
 */
const REGISTRY = 'http://localhost:5173/api/v1/admin/repositories'

beforeAll(() => mockServer.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  mockServer.resetHandlers()
  setMockScenario('default')
  // handler 里的会话态（新增 / 删除）不会被 resetHandlers 还原
  resetSessionRepositories()
})
afterAll(() => mockServer.close())

interface Envelope {
  success: boolean
  code: number
  message: string
  data: unknown
}

async function post(path: string, body?: unknown) {
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  })

  return { status: res.status, json: (await res.json()) as Envelope }
}

describe('仓库配置端点（0.4.0）', () => {
  it('列表返回**普通数组**，且响应里没有口令与密文', async () => {
    const { status, json } = await post(`${REGISTRY}/list`, {})

    expect(status).toBe(200)

    const items = json.data as Record<string, unknown>[]
    expect(Array.isArray(items)).toBe(true)
    expect(items).toHaveLength(MOCK_REPOSITORIES_DEFAULT.length)

    for (const item of items) {
      // 密文进了响应就等于口令进了响应——抓一次包就能拿到一把通用钥匙
      expect(item).not.toHaveProperty('password')
      expect(item).not.toHaveProperty('encryptedPassword')
      expect(Object.keys(item).sort()).toEqual(
        [
          'createdAt',
          'description',
          'hasCredentials',
          'id',
          'lastModifiedAt',
          'name',
          'serverAddress',
          'username'
        ].sort()
      )
    }
  })

  it('两种形态都在夹具里：带凭据与匿名（账号列的两个分支）', async () => {
    const { json } = await post(`${REGISTRY}/list`, {})
    const items = json.data as { hasCredentials: boolean; username: string }[]

    expect(items.some((item) => item.hasCredentials && item.username.length > 0)).toBe(true)
    expect(items.some((item) => !item.hasCredentials && item.username === '')).toBe(true)
  })

  it('别名重复 → 1801 / 400（不区分大小写）', async () => {
    const { status, json } = await post(`${REGISTRY}/add`, {
      name: 'Harbor-Prod',
      serverAddress: 'another.local:5000'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.RepositoryNameAlreadyExists)
  })

  it('地址重复 → 1802 / 400（同址两条配置会让凭据选择变成随机）', async () => {
    const { status, json } = await post(`${REGISTRY}/add`, {
      name: 'harbor-dup',
      serverAddress: 'harbor.local:5000'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.RepositoryAddressAlreadyExists)
  })

  it('账号与口令只给一个 → 1000 / 400', async () => {
    const { status, json } = await post(`${REGISTRY}/add`, {
      name: 'half-cred',
      serverAddress: 'half.local:5000',
      username: 'someone'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('新增后列表立刻能看到，且地址被规范化（去 scheme / 去尾斜杠 / 小写）', async () => {
    const created = await post(`${REGISTRY}/add`, {
      name: 'ci-registry',
      serverAddress: 'HTTPS://Ci.Local:5000/',
      username: 'ci',
      password: 'secret'
    })

    expect(created.status).toBe(200)
    expect((created.json.data as { serverAddress: string }).serverAddress).toBe('ci.local:5000')

    const { json } = await post(`${REGISTRY}/list`, {})
    expect((json.data as { name: string }[]).some((item) => item.name === 'ci-registry')).toBe(true)
  })

  it('删除不存在的配置 → 1800 / 404', async () => {
    const { status, json } = await post(`${REGISTRY}/remove`, {
      id: '00000000-0000-0000-0000-0000000000ff'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.RepositoryNotFound)
  })

  it('测试已保存的配置：带凭据 → authenticated=true', async () => {
    const { status, json } = await post(`${REGISTRY}/test`, {
      id: MOCK_REPOSITORIES_DEFAULT[0].id
    })

    expect(status).toBe(200)
    expect(json.data).toMatchObject({ reachable: true, authenticated: true })
  })

  it('测试匿名配置 → reachable=true 但 authenticated=false（不是失败）', async () => {
    const { status, json } = await post(`${REGISTRY}/test`, {
      id: MOCK_REPOSITORIES_DEFAULT[1].id
    })

    // 这条压的是「连通」与「认证」必须分开：压成一个布尔值，
    // 界面就只能显示「测试失败」，用户不知道该查网络还是该补账号。
    expect(status).toBe(200)
    expect(json.data).toMatchObject({ reachable: true, authenticated: false })
  })

  it('测试尚未保存的地址：denied* → 1804 / 400，unreachable* → 1805 / 502', async () => {
    const denied = await post(`${REGISTRY}/test`, { serverAddress: 'denied.local:5000' })
    expect(denied.status).toBe(400)
    expect(denied.json.code).toBe(ErrorCode.RepositoryAuthFailed)

    // ⚠️ 两个码必须是**不同**的：前者的处置是重填口令，后者是查网络与防火墙
    const unreachable = await post(`${REGISTRY}/test`, { serverAddress: 'unreachable.local:5000' })
    expect(unreachable.status).toBe(502)
    expect(unreachable.json.code).toBe(ErrorCode.RepositoryUnreachable)
  })
})
