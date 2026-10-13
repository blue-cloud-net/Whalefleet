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
/**
 * 容器端点的契约守卫（0.0.11）。
 *
 * 重点不在「返回了数据」，而在**后端那几个容易被前端忽略的语义**被复刻了：
 * `pageSize` 夹紧后回显、`force` 缺失时的 409、`tail` 越界。
 * 前端若按自己的请求值去算分页，下面第一条就会挂。
 */
describe('容器端点', () => {
  it('列表回显夹紧后的 pageSize（请求 5000 → 100）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/list', {
      pageIndex: 0,
      pageSize: 5000
    })

    expect(status).toBe(200)

    const data = json.data as { pageSize: number; pageIndex: number; total: number }
    expect(data.pageSize).toBe(100)
    expect(data.pageIndex).toBe(0)
    expect(data.total).toBeGreaterThan(0)
  })

  it('all=false 只列运行中的容器', async () => {
    const all = await post('/api/v1/hosts/local/containers/list', { all: true })
    const runningOnly = await post('/api/v1/hosts/local/containers/list', { all: false })

    const allData = all.json.data as { items: { state: string }[] }
    const runningData = runningOnly.json.data as { items: { state: string }[] }

    expect(allData.items.length).toBeGreaterThan(runningData.items.length)
    expect(runningData.items.every((item) => item.state === 'running')).toBe(true)
  })

  it('不带 force 删除运行中的容器 → 409 / 1404', async () => {
    // 这条语义是前端「先停止容器」提示的唯一来源，mock 不复刻它就永远测不到。
    const list = await post('/api/v1/hosts/local/containers/list', { all: true })
    const items = (list.json.data as { items: { id: string; state: string }[] }).items
    const running = items.find((item) => item.state === 'running')

    expect(running, '夹具里必须有一个运行中的容器，否则本条测不到 409').toBeDefined()

    const { status, json } = await post('/api/v1/hosts/local/containers/remove', {
      containerId: running!.id
    })

    expect(status).toBe(409)
    expect(json.code).toBe(ErrorCode.DockerResourceConflict)
  })

  it('带 force 删除运行中的容器 → 成功', async () => {
    const list = await post('/api/v1/hosts/local/containers/list', { all: true })
    const items = (list.json.data as { items: { id: string; state: string }[] }).items
    const running = items.find((item) => item.state === 'running')!

    const { status, json } = await post('/api/v1/hosts/local/containers/remove', {
      containerId: running.id,
      force: true
    })

    expect(status).toBe(200)
    expect(json.success).toBe(true)
  })

  it('logs 的 tail 越界 → 1000（不是静默截断）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/logs', {
      containerId: 'whatever',
      tail: 10001
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('create 成功时回带 warnings（是提示，不是错误）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/create', {
      image: 'nginx:alpine'
    })

    expect(status).toBe(200)

    const data = json.data as { containerId: string; warnings: string[] }
    expect(data.containerId).toBeTruthy()
    expect(Array.isArray(data.warnings)).toBe(true)
    expect(data.warnings.length).toBeGreaterThan(0)
  })

  it('容器不存在 → 1401 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/inspect', {
      containerId: 'no-such-container'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.DockerResourceNotFound)
  })

  it('empty 场景下列表为空数组而不是报错', async () => {
    setMockScenario('empty')

    const { status, json } = await post('/api/v1/hosts/local/containers/list', { all: true })

    expect(status).toBe(200)
    expect((json.data as { items: unknown[]; total: number }).items).toEqual([])
    expect((json.data as { total: number }).total).toBe(0)
  })
})

/**
 * 镜像端点的契约守卫（0.0.12）。
 *
 * 与容器那组同一个意图：**后端那几个容易被前端忽略的语义**必须被复刻，
 * 否则对应的界面分支永远走查不到。最典型的是「多标签镜像删掉一个标签时本体仍在」——
 * 不模拟它，界面上「已解绑标签」那段提示就是死代码。
 */
describe('镜像端点', () => {
  it('列表回显夹紧后的 pageSize（请求 5000 → 100）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/list', { pageSize: 5000 })

    expect(status).toBe(200)

    const data = json.data as { pageSize: number; total: number }
    expect(data.pageSize).toBe(100)
    expect(data.total).toBeGreaterThan(0)
  })

  it('search 在服务端过滤，大小写不敏感', async () => {
    const { json } = await post('/api/v1/hosts/local/images/list', { search: 'NGINX' })

    const items = (json.data as { items: { repoTags: string[] }[] }).items
    expect(items.length).toBeGreaterThan(0)
    expect(items.every((item) => item.repoTags.some((tag) => /nginx/i.test(tag)))).toBe(true)
  })

  it('悬挂镜像以空 repoTags 表示（后端已过滤掉 <none> 标签）', async () => {
    const { json } = await post('/api/v1/hosts/local/images/list', {})
    const items = (json.data as { items: { repoTags: string[] }[] }).items

    expect(items.some((item) => item.repoTags.length === 0)).toBe(true)
  })

  it('不带 force 删除被容器引用的镜像 → 409 / 1404', async () => {
    const list = await post('/api/v1/hosts/local/images/list', {})
    const items = (list.json.data as { items: { repoTags: string[]; containerCount: number }[] })
      .items
    const inUse = items.find((item) => item.containerCount > 0)

    expect(inUse, '夹具里必须有一个被容器引用的镜像，否则本条测不到 409').toBeDefined()

    const { status, json } = await post('/api/v1/hosts/local/images/remove', {
      imageId: inUse!.repoTags[0]
    })

    expect(status).toBe(409)
    expect(json.code).toBe(ErrorCode.DockerResourceConflict)
  })

  it('删除多标签镜像的某个标签：只解绑该标签，镜像本体仍在', async () => {
    const list = await post('/api/v1/hosts/local/images/list', {})
    const items = (list.json.data as { items: { repoTags: string[] }[] }).items
    const multi = items.find((item) => item.repoTags.length > 1)

    expect(multi, '夹具里必须有一个多标签镜像，否则本条测不到「只解绑」').toBeDefined()

    const removed = await post('/api/v1/hosts/local/images/remove', {
      imageId: multi!.repoTags[0],
      force: true
    })

    expect(removed.status).toBe(200)
    expect((removed.json.data as { untagged: string[] }).untagged).toEqual([multi!.repoTags[0]])
    // deleted 为空 = 镜像本体未被删除，这正是界面必须说明的情形
    expect((removed.json.data as { deleted: string[] }).deleted).toEqual([])

    const after = await post('/api/v1/hosts/local/images/list', {})
    const afterItems = (after.json.data as { items: { id: string; repoTags: string[] }[] }).items
    const stillThere = afterItems.find((item) => item.id === multi!.id)

    expect(stillThere?.repoTags).toEqual(multi!.repoTags.slice(1))
  })

  it('image 已含标签又另传 tag → 1000（不是拼成 nginx:1.27:alpine）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/pull', {
      image: 'nginx:1.27',
      tag: 'alpine'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('拉取失败两条路径：不存在 → 1401；仓库拒绝 → 1402', async () => {
    const missing = await post('/api/v1/hosts/local/images/pull', { image: 'missing/repo' })
    expect(missing.status).toBe(404)
    expect(missing.json.code).toBe(ErrorCode.DockerResourceNotFound)

    const denied = await post('/api/v1/hosts/local/images/pull', { image: 'denied/private' })
    expect(denied.status).toBe(500)
    expect(denied.json.code).toBe(ErrorCode.DockerApiFailed)
  })

  it('拉取成功后列表里立刻能看到它（mock 必须有记忆）', async () => {
    const pulled = await post('/api/v1/hosts/local/images/pull', { image: 'busybox', tag: '1.36' })

    expect(pulled.status).toBe(200)
    const reference = (pulled.json.data as { reference: string }).reference
    expect(reference).toBe('busybox:1.36')

    const list = await post('/api/v1/hosts/local/images/list', {})
    const items = (list.json.data as { items: { repoTags: string[] }[] }).items
    expect(items.some((item) => item.repoTags.includes('busybox:1.36'))).toBe(true)
  })

  it('镜像不存在 → 1401 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/inspect', {
      imageId: 'no-such-image'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.DockerResourceNotFound)
  })

  it('empty 场景下镜像列表为空数组而不是报错', async () => {
    setMockScenario('empty')

    const { status, json } = await post('/api/v1/hosts/local/images/list', {})

    expect(status).toBe(200)
    expect((json.data as { items: unknown[] }).items).toEqual([])
  })
})

/**
 * 用户管理与主机分组的契约守卫（0.0.13）。
 *
 * 这批用例守的是「界面上那些提示有没有来源」：
 * 删自己被挡、最后一个管理员删不掉、原密码错返回 **1000 而不是 401**、
 * 删除分组不删主机……不复刻任何一条，对应的界面分支都成了永远走不到的死代码。
 */
describe('用户管理端点', () => {
  it('列表回显夹紧后的 pageSize（请求 5000 → 100）', async () => {
    const { status, json } = await post('/api/v1/admin/users/list', { pageSize: 5000 })

    expect(status).toBe(200)
    expect((json.data as { pageSize: number }).pageSize).toBe(100)
    expect((json.data as { total: number }).total).toBeGreaterThan(0)
  })

  it('用户名重复 → 1200 / 400', async () => {
    const { status, json } = await post('/api/v1/admin/users/create', {
      username: 'admin',
      password: 'StrongP@ss123'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.UsernameAlreadyExists)
  })

  it('密码强度不足 → 1202 / 400', async () => {
    const { status, json } = await post('/api/v1/admin/users/create', {
      username: 'newcomer',
      password: 'short'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.WeakPassword)
  })

  it('创建成功后可立刻在列表里看到（mock 必须有记忆）', async () => {
    const created = await post('/api/v1/admin/users/create', {
      username: 'deploy-bot',
      password: 'StrongP@ss123'
    })

    expect(created.status).toBe(200)
    // 省略 role 时取默认角色（当前为 auditor）
    expect((created.json.data as { role: string }).role).toBe('auditor')

    const list = await post('/api/v1/admin/users/list', {})
    expect(
      (list.json.data as { items: { username: string }[] }).items.some(
        (user) => user.username === 'deploy-bot'
      )
    ).toBe(true)
  })

  it('删除当前登录账号 → 1000 / 400（后端护栏）', async () => {
    const list = await post('/api/v1/admin/users/list', {})
    const self = (list.json.data as { items: { id: string; username: string }[] }).items.find(
      (user) => user.username === 'admin'
    )

    const { status, json } = await post('/api/v1/admin/users/delete', { userId: self!.id })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('删除最后一个管理员 → 1000 / 400', async () => {
    // 先把唯一的管理员之外没有其他 admin，因此删它必然命中「最后一个管理员」
    const list = await post('/api/v1/admin/users/list', {})
    const admins = (list.json.data as { items: { id: string; role: string }[] }).items.filter(
      (user) => user.role === 'admin'
    )

    expect(admins).toHaveLength(1)

    const { status, json } = await post('/api/v1/admin/users/delete', { userId: admins[0].id })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('自助改密：原密码错误 → 1000 而不是 401', async () => {
    // ⚠️ 401 会被前端拦截器当成「会话过期」去刷新并重放，
    // 用在这里会把「输错原密码」表现成一连串莫名其妙的重定向。
    const { status, json } = await post('/api/v1/admin/users/update-password', {
      oldPassword: 'wrong-password',
      newPassword: 'AnotherP@ss123'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('自助改密：原密码正确 → 成功', async () => {
    const { status, json } = await post('/api/v1/admin/users/update-password', {
      oldPassword: 'demo1234',
      newPassword: 'AnotherP@ss123'
    })

    expect(status).toBe(200)
    expect(json.success).toBe(true)
  })

  it('管理员重置他人不需要原密码', async () => {
    const list = await post('/api/v1/admin/users/list', {})
    const target = (list.json.data as { items: { id: string; username: string }[] }).items.find(
      (user) => user.username === 'ops-zhang'
    )

    const { status } = await post('/api/v1/admin/users/update-password', {
      userId: target!.id,
      newPassword: 'ResetP@ss1234'
    })

    expect(status).toBe(200)
  })
})

describe('主机分组端点', () => {
  it('读端点返回三个内置分组，且带主机数', async () => {
    const { status, json } = await post('/api/v1/host-groups/list')

    expect(status).toBe(200)

    const groups = json.data as { name: string; hostCount: number }[]
    expect(groups.map((group) => group.name)).toEqual(['production', 'staging', 'local'])
    expect(groups.find((group) => group.name === 'local')!.hostCount).toBe(1)
  })

  it('分组名重复 → 1304 / 400', async () => {
    const { status, json } = await post('/api/v1/admin/host-groups/create', { name: 'production' })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.GroupNameAlreadyExists)
  })

  it('分组不存在 → 1305 / 404', async () => {
    const { status, json } = await post(
      '/api/v1/admin/host-groups/00000000-0000-0000-0000-00000000dead/update',
      { name: 'whatever' }
    )

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.GroupNotFound)
  })

  it('只改描述不改名字时不会与自己冲突', async () => {
    // 重名检查必须排除自己，否则「保存一下」都会被自己的旧名字挡住
    const { status } = await post(
      '/api/v1/admin/host-groups/00000000-0000-0000-0000-0000000000a1/update',
      { description: '核心交易集群' }
    )

    expect(status).toBe(200)
  })

  it('主机归属：目标分组不存在 → 1305 / 404', async () => {
    const { status, json } = await post('/api/v1/admin/hosts/local/group', {
      groupId: '00000000-0000-0000-0000-00000000dead'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.GroupNotFound)
  })

  it('主机归属：可移入分组，也可用 null 移出', async () => {
    const staging = '00000000-0000-0000-0000-0000000000a2'

    const countOf = async (name: string): Promise<number> => {
      const list = await post('/api/v1/host-groups/list')
      return (list.json.data as { name: string; hostCount: number }[]).find(
        (group) => group.name === name
      )!.hostCount
    }

    // 夹具里 staging-01 本来就在 staging 组，因此移入后是 2 台
    expect(await countOf('staging')).toBe(1)

    expect((await post('/api/v1/admin/hosts/local/group', { groupId: staging })).status).toBe(200)
    expect(await countOf('staging')).toBe(2)
    expect(await countOf('local')).toBe(0)

    // 移出分组：groupId 显式为 null。这正是它单设端点而不是塞进 hosts/update 的理由——
    // 那边的「未提供表示不改」语义分不清「没给 groupId」与「给了 null」。
    expect((await post('/api/v1/admin/hosts/local/group', { groupId: null })).status).toBe(200)
    expect(await countOf('staging')).toBe(1)
    expect(await countOf('local')).toBe(0)
  })

  it('删除分组不删主机：主机回到未分组，并回带被移出的台数', async () => {
    const staging = '00000000-0000-0000-0000-0000000000a2'
    // 夹具里 staging-01 已在 staging 组，再把 local 移进去 → 该组共 2 台
    await post('/api/v1/admin/hosts/local/group', { groupId: staging })

    const removed = await post(`/api/v1/admin/host-groups/${staging}/delete`)

    expect(removed.status).toBe(200)
    expect(removed.json.data).toBe(2)

    // 主机还在（这条正是界面提示「已把 N 台主机移出该分组」的依据）
    const hosts = await post('/api/v1/hosts/list')
    const local = (hosts.json.data as { id: string; groupId?: string }[]).find(
      (host) => host.id === 'local'
    )
    expect(local).toBeDefined()
    expect(local!.groupId).toBeUndefined()
  })
})

describe('主机管理端点', () => {
  it('管理列表用主键做 id，并用 null 表示「未探测」', async () => {
    const { status, json } = await post('/api/v1/admin/hosts/list')

    expect(status).toBe(200)

    const hosts = json.data as { id: string; name: string; reachable: boolean | null }[]
    const local = hosts.find((host) => host.name === 'local')!

    // ⚠️ 这里的 id 是主键（Guid），与 /hosts/list 的 id（主机名）不是同一个东西
    expect(local.id).toMatch(/^[0-9a-f-]{36}$/)
    expect(typeof local.reachable).toBe('boolean')
  })

  it('创建主机不探活：reachable 为 null 而不是 false', async () => {
    const created = await post('/api/v1/admin/hosts/create', {
      name: 'edge-01',
      type: 'tcp',
      endpoint: 'tcp://10.0.0.42:2375'
    })

    expect(created.status).toBe(200)
    // false 会让界面显示成「离线」，把「没测」误报成故障
    expect((created.json.data as { reachable: boolean | null }).reachable).toBeNull()
  })

  it('主机名重复 → 1303 / 400', async () => {
    const { status, json } = await post('/api/v1/admin/hosts/create', {
      name: 'local',
      type: 'socket',
      endpoint: '/var/run/docker.sock'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.HostNameAlreadyExists)
  })

  it('测试连通性：连不上也是 200 + data=false，不是错误响应', async () => {
    const unreachable = await post('/api/v1/admin/hosts/test', {
      type: 'tcp',
      endpoint: 'unreachable://10.0.0.99:2375'
    })

    expect(unreachable.status).toBe(200)
    expect(unreachable.json.data).toBe(false)

    const reachable = await post('/api/v1/admin/hosts/test', {
      type: 'socket',
      endpoint: '/var/run/docker.sock'
    })

    expect(reachable.json.data).toBe(true)
  })
})
