/**
 * @vitest-environment node
 *
 * 刻意用 **node 环境**而非默认的 jsdom：
 * jsdom 自带一套 `AbortSignal` 实现，与 Node 原生 `fetch` 不是同一个实例，
 * MSW 的 fetch 拦截器会因此抛
 * `RequestInit: Expected signal ("AbortSignal {}") to be an instance of AbortSignal`。
 *
 * 本文件只验证 HTTP 契约，完全不碰 DOM，切到 node 环境最合适；
 * 也避免为「让 mock 能跑」而往 `tests/setup.ts` 里塞 AbortSignal polyfill。
 */
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { mockServer } from '../src/mocks/node'
import { setMockScenario } from '../src/mocks/index'
import { ERROR_HTTP_STATUS, ERROR_MESSAGES, ErrorCode, httpStatusOf } from '../src/mocks/envelope'
import type { HostOverview } from '../src/types/api'

/**
 * mock 契约守卫。
 *
 * ## 为什么必须有这个文件
 *
 * `docs/api-spec.md` 自称错误码的**唯一事实源**，并要求四处同步：
 * spec 表 → 后端 `ErrorCodes` → 后端 `ErrorCodeCatalog` → 前端 `types/api.ts`。
 * 引入 mock 后多出**第五处**（`src/mocks/envelope.ts`）。
 *
 * 本轮决定「手写 mock + 守卫测试」而非先生成 OpenAPI，因此**漂移只能靠测试暴露**：
 * 任何人在后端加了错误码却忘了改这里，本文件就会挂。
 */
beforeAll(() => mockServer.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  mockServer.resetHandlers()
  setMockScenario('default')
})
afterAll(() => mockServer.close())

/**
 * node 环境没有浏览器的 document.baseURL，相对路径无法解析，
 * 因此测试用一个固定的绝对 base —— 这与真实运行时的同源相对路径等价
 * （mock 匹配的是 path，与 origin 无关）。
 */
const BASE = 'http://localhost:5173'

async function post(path: string, body?: unknown) {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  })
  return { status: res.status, json: (await res.json()) as Record<string, unknown> }
}

describe('信封格式（逐字复刻后端 ApiResult）', () => {
  it('成功响应四个字段齐全，code 为 0', async () => {
    const { status, json } = await post('/api/v1/hosts/list')
    expect(status).toBe(200)
    expect(Object.keys(json).sort()).toEqual(['code', 'data', 'message', 'success'])
    expect(json.success).toBe(true)
    expect(json.code).toBe(0)
  })

  it('失败响应显式含 data: null（后端不开 WhenWritingNull，该字段不会被省略）', async () => {
    const { json } = await post('/api/v1/auth/login', { username: 'admin' })
    expect(json.success).toBe(false)
    expect(json).toHaveProperty('data')
    expect(json.data).toBeNull()
  })

  it('失败响应同样四字段齐全', async () => {
    const { json } = await post('/api/v1/auth/login', {})
    expect(Object.keys(json).sort()).toEqual(['code', 'data', 'message', 'success'])
  })
})

describe('错误码 → HTTP 状态码（与后端 ErrorCodeCatalog + ErrorHttpStatus 逐码对照）', () => {
  /**
   * 期望值在此**显式写死**而非引用 ERROR_HTTP_STATUS。
   *
   * 若直接引用被测对象，测试就成了同义反复：改了映射表，测试照样绿。
   * 写死的数组还让「后端加了码却没改这里」变成一次显眼的 diff。
   */
  const EXPECTED: [number, number][] = [
    [0, 200],
    [1000, 400],
    [1001, 404],
    [1002, 500],
    [1003, 408],
    [1100, 401],
    [1101, 401],
    [1102, 401],
    [1103, 429],
    [1104, 401],
    [1200, 400],
    [1201, 404],
    [1202, 400],
    [1300, 404],
    [1301, 502],
    [1302, 400],
    [1303, 400],
    [1401, 404],
    [1402, 500],
    [1403, 408],
    [1500, 403],
    [1501, 403],
    [1502, 403],
    [9999, 500]
  ]

  it.each(EXPECTED)('错误码 %i → HTTP %i', (code, status) => {
    expect(ERROR_HTTP_STATUS[code]).toBe(status)
    expect(httpStatusOf(code)).toBe(status)
  })

  it('每个已登记的错误码都有状态码映射（漏登记就是未来的 500 陷阱）', () => {
    for (const code of Object.values(ErrorCode)) {
      expect(ERROR_HTTP_STATUS[code], `错误码 ${code} 未映射 HTTP 状态`).toBeTypeOf('number')
    }
  })

  it('每个已登记的错误码都有默认文案（否则会退回后端的「请求处理失败」）', () => {
    for (const code of Object.values(ErrorCode)) {
      expect(ERROR_MESSAGES[code], `错误码 ${code} 缺默认文案`).toBeTruthy()
    }
  })

  it('未登记的错误码一律兜底 500（与后端 Classify 的兜底一致，不按数值分段推断）', () => {
    expect(httpStatusOf(7777)).toBe(500)
    expect(httpStatusOf(-1)).toBe(500)
  })
})

describe('主机列表契约', () => {
  it('返回字段与 HostOverview 类型对齐', async () => {
    const { json } = await post('/api/v1/hosts/list')
    const hosts = json.data as HostOverview[]
    expect(Array.isArray(hosts)).toBe(true)
    expect(hosts).toHaveLength(3)
    for (const h of hosts) {
      expect(typeof h.id).toBe('string')
      expect(typeof h.name).toBe('string')
      expect(['socket', 'tcp']).toContain(h.type)
      expect(typeof h.endpoint).toBe('string')
      expect(typeof h.enabled).toBe('boolean')
    }
  })

  it('列表内置 status，但不含 version / counts（两者走聚合端点）', async () => {
    const { json } = await post('/api/v1/hosts/list')
    const hosts = json.data as HostOverview[]
    expect(hosts.some((h) => h.status === 'online')).toBe(true)
    expect(hosts.every((h) => h.counts === undefined)).toBe(true)
  })
})

describe('主机聚合端点的错误分支（复刻 CurrentHostMiddleware 与 IDockerHostAccessor）', () => {
  it('主机不存在 → 1300 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/nope/overview')
    expect(status).toBe(404)
    expect(json.code).toBe(1300)
    expect(json.data).toBeNull()
  })

  it('主机已停用 → 1302 / 400', async () => {
    setMockScenario('disabled')
    const { status, json } = await post('/api/v1/hosts/retired-01/overview')
    expect(status).toBe(400)
    expect(json.code).toBe(1302)
  })

  it('在线主机返回 version；离线主机返回空（与真实行为一致）', async () => {
    const online = await post('/api/v1/hosts/local/overview')
    expect((online.json.data as HostOverview).version?.version).toBe('27.3.1')

    const offline = await post('/api/v1/hosts/staging-01/overview')
    expect((offline.json.data as HostOverview).version).toBeUndefined()
  })
})

describe('令牌轮换语义（前端最容易写错的地方）', () => {
  it('刷新会轮换 refresh token，旧值再拿来换 → 1102 / 401', async () => {
    const login = await post('/api/v1/auth/login', { username: 'admin', password: 'demo1234' })
    const first = login.json.data as { accessToken: string; refreshToken: string }

    const refreshed = await post('/api/v1/auth/refresh', { refreshToken: first.refreshToken })
    expect(refreshed.status).toBe(200)
    const second = refreshed.json.data as { refreshToken: string }
    expect(second.refreshToken).not.toBe(first.refreshToken)

    // 重放旧令牌 → 必须失败，否则前端「两处都用旧值刷新」这类 bug 测不出来
    const replay = await post('/api/v1/auth/refresh', { refreshToken: first.refreshToken })
    expect(replay.status).toBe(401)
    expect(replay.json.code).toBe(1102)
  })

  it('并发刷新时只有第一个成功，后到的因令牌已轮换而 1102', async () => {
    const login = await post('/api/v1/auth/login', { username: 'admin', password: 'demo1234' })
    const { refreshToken } = login.json.data as { refreshToken: string }

    const [a, b] = await Promise.all([
      post('/api/v1/auth/refresh', { refreshToken }),
      post('/api/v1/auth/refresh', { refreshToken })
    ])

    const statuses = [a.status, b.status].sort()
    expect(statuses).toEqual([200, 401])
  })

  it('登录响应结构严格为 { accessToken, refreshToken, expiresIn, user }', async () => {
    const { json } = await post('/api/v1/auth/login', { username: 'admin', password: 'demo1234' })
    const result = json.data as Record<string, unknown>
    expect(Object.keys(result).sort()).toEqual([
      'accessToken',
      'expiresIn',
      'refreshToken',
      'user'
    ])
    expect(typeof result.expiresIn).toBe('number')
    const user = result.user as Record<string, unknown>
    expect(typeof user.username).toBe('string')
    expect(typeof user.role).toBe('string')
  })
})

describe('场景切换', () => {
  it('empty 场景返回零主机（供空状态走查）', async () => {
    setMockScenario('empty')
    const { json } = await post('/api/v1/hosts/list')
    expect(json.data).toEqual([])
  })

  it('error 场景列表整体 9999 / 500（供错误态走查）', async () => {
    setMockScenario('error')
    const { status, json } = await post('/api/v1/hosts/list')
    expect(status).toBe(500)
    expect(json.code).toBe(9999)
  })

  it('counts 场景带出资源计数（供 4 个磁贴的有值形态走查）', async () => {
    setMockScenario('counts')
    const overview = await post('/api/v1/hosts/local/overview')
    expect((overview.json.data as HostOverview).counts?.containers).toBe(12)
  })
})