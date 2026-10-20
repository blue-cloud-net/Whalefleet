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
import { resetSessionContainers } from '../src/mocks/handlers/containers'
import { resetSessionHosts } from '../src/mocks/handlers/hosts'
import { resetSessionImages } from '../src/mocks/handlers/images'
import { resetSessionRoles } from '../src/mocks/handlers/permissions'
import { resetSessionUsers } from '../src/mocks/handlers/users'
import { MOCK_CONTAINERS_DEFAULT } from '../src/mocks/data/containers'
import { MOCK_IMAGES_DEFAULT } from '../src/mocks/data/images'
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

  // ⚠️ `resetHandlers()` 只还原 handler，**不还原 handler 里的会话态**
  // （模块级 Map / Set）。不清的话，「撤销权限」这类写操作会漏到下一个用例，
  // 症状是随机顺序下某条用例莫名失败——而且越靠后的用例越容易被污染。
  resetSessionContainers()
  resetSessionHosts()
  resetSessionImages()
  resetSessionRoles()
  resetSessionUsers()
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
    [1503, 404],
    [1510, 404],
    [1511, 400],
    [1512, 400],
    [1513, 400],
    [1514, 400],
    [1515, 404],
    [1800, 404],
    [1801, 400],
    [1802, 400],
    [1803, 400],
    [1804, 400],
    [1805, 502],
    [1806, 500],
    [1807, 500],
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
    // 0.1.0 起是**角色数组 + 全局有效权限集**，不再是单值 role
    expect(Array.isArray(user.roles)).toBe(true)
    expect(Array.isArray(user.permissions)).toBe(true)
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
    // 断言「磁贴上的数字 = 容器列表的实际条数」这个**关系**，而不是某个具体数字：
    // 磁贴和列表读同一份 mock 数据源，写死数值只会在数据集扩容时变成假失败。
    const counts = (overview.json.data as HostOverview).counts
    expect(counts?.containers).toBe(MOCK_CONTAINERS_DEFAULT.length)
    expect(counts?.images).toBe(MOCK_IMAGES_DEFAULT.length)
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
    const all = await post('/api/v1/hosts/local/containers/list', { all: true, pageSize: 100 })
    const runningOnly = await post('/api/v1/hosts/local/containers/list', {
      all: false,
      pageSize: 100
    })

    const allData = all.json.data as { items: { state: string }[]; total: number }
    const runningData = runningOnly.json.data as { items: { state: string }[]; total: number }

    // 比 total 而不是 items.length：列表超过一页后 items.length 恒等于 pageSize，
    // 两个数字永远相等，这条断言就再也测不出东西了。
    expect(allData.total).toBeGreaterThan(runningData.total)
    expect(runningData.items.every((item) => item.state === 'running')).toBe(true)
    expect(runningData.total).toBeGreaterThan(0)
  })

  /** 排序契约：复刻后端 `ContainerService.Sort`，规则与镜像列表同构。 */
  it('sortBy=name & sortDir=asc 按名称升序', async () => {
    const { json } = await post('/api/v1/hosts/local/containers/list', {
      sortBy: 'name',
      sortDir: 'asc',
      all: true,
      pageSize: 100
    })

    const names = (json.data as { items: { name: string }[] }).items.map((item) => item.name)
    expect(names.length).toBeGreaterThan(1)
    expect(names).toEqual([...names].sort((a, b) => a.localeCompare(b)))
  })

  it('容器排序后翻页不重不漏', async () => {
    const first = await post('/api/v1/hosts/local/containers/list', {
      sortBy: 'name',
      sortDir: 'asc',
      all: true,
      pageIndex: 0,
      pageSize: 2
    })
    const second = await post('/api/v1/hosts/local/containers/list', {
      sortBy: 'name',
      sortDir: 'asc',
      all: true,
      pageIndex: 1,
      pageSize: 2
    })

    const page1 = first.json.data as { items: { id: string }[]; total: number }
    const page2 = second.json.data as { items: { id: string }[]; total: number }

    expect(page2.total).toBe(page1.total)

    const ids = [...page1.items, ...page2.items].map((item) => item.id)
    expect(new Set(ids).size).toBe(ids.length)
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

  /**
   * 夹具的一致性守卫（不是端点契约，而是**夹具之间**的自洽）。
   *
   * 列表里每一行都必须能被 inspect 查到详情。数据集扩容后这条极易破：
   * 长尾条目只加进 `MOCK_*_DEFAULT`（列表）而忘了加进 `MOCK_*_DETAILS`（详情），
   * 症状是「列表渲染得好好的，点进去却报资源不存在」——看起来像前端的 bug，
   * 实际上只是 mock 自己漏了一条。逐条点页面根本发现不了。
   */
  it('每一条列表项都能 inspect 到详情（夹具自洽守卫）', async () => {
    const containers = await post('/api/v1/hosts/local/containers/list', {
      all: true,
      pageSize: 100
    })
    const items = (containers.json.data as { items: { id: string; name: string }[] }).items
    expect(items.length).toBeGreaterThan(0)

    for (const item of items) {
      const { status, json } = await post('/api/v1/hosts/local/containers/inspect', {
        containerId: item.id
      })
      expect(status, `容器 ${item.name} 在列表里但 inspect 不到`).toBe(200)
      expect(json.success).toBe(true)
    }
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

  /**
   * 排序契约：mock 必须复刻后端 `ImageService.Sort` 的三条规则，
   * 否则前端的排序功能在 mock 模式下「测不出来」，上线才发现是坏的。
   */
  it('sortBy=size 时按大小升序，且排序发生在分页之前', async () => {
    const { json } = await post('/api/v1/hosts/local/images/list', {
      sortBy: 'size',
      sortDir: 'asc',
      pageSize: 100
    })

    const data = json.data as { items: { size: number }[]; total: number }
    expect(data.total).toBeGreaterThan(1)

    const sizes = data.items.map((item) => item.size)
    // 降序才需要反着比；升序应严格非递减
    expect(sizes).toEqual([...sizes].sort((a, b) => a - b))
  })

  it('sortBy=size & sortDir=desc 与 asc 结果互为倒序', async () => {
    const asc = await post('/api/v1/hosts/local/images/list', {
      sortBy: 'size',
      sortDir: 'asc',
      pageSize: 100
    })
    const desc = await post('/api/v1/hosts/local/images/list', {
      sortBy: 'size',
      sortDir: 'desc',
      pageSize: 100
    })

    const a = (asc.json.data as { items: { size: number }[] }).items.map((item) => item.size)
    const d = (desc.json.data as { items: { size: number }[] }).items.map((item) => item.size)

    // 比 **size** 而不是 id：后端 ImageService.Sort 的 id 兜底键**不随方向翻转**
    // （`descending` 只作用在主键上），所以只要有两个镜像体积相同，
    // 两个方向的 id 序列就不是互为倒序的——那是后端的既定行为，不是缺陷。
    // 主键序列仍然必须互为倒序。
    expect(d).toEqual([...a].reverse())
  })

  it('排序后翻页不重不漏（total 不变，第 2 页接在第 1 页之后）', async () => {
    const first = await post('/api/v1/hosts/local/images/list', {
      sortBy: 'size',
      sortDir: 'asc',
      pageIndex: 0,
      pageSize: 2
    })
    const second = await post('/api/v1/hosts/local/images/list', {
      sortBy: 'size',
      sortDir: 'asc',
      pageIndex: 1,
      pageSize: 2
    })

    const page1 = first.json.data as { items: { id: string }[]; total: number }
    const page2 = second.json.data as { items: { id: string }[]; total: number }

    // total 是**排序后**的总数，两页必须一致；否则「翻页翻到重复/空」就是排序漏在分页之后了
    expect(page2.total).toBe(page1.total)

    const ids = [...page1.items, ...page2.items].map((item) => item.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('未知 sortBy 不报错，退回 created（版本错配不该让整个列表 400）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/list', {
      sortBy: 'not-a-real-field'
    })

    expect(status).toBe(200)
    expect((json.data as { items: unknown[] }).items.length).toBeGreaterThan(0)
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

    // pageSize 100：新拉的镜像追加到列表末尾，列表超过一页时默认的 20 条里看不到它。
    // 真实后端也是这样（按创建时间降序，新拉的排最后），mock 不假装把它插到第一页。
    const list = await post('/api/v1/hosts/local/images/list', { pageSize: 100 })
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

  /** 镜像侧的夹具自洽守卫，理由同容器那条。 */
  it('每一条列表项都能 inspect 到详情（夹具自洽守卫）', async () => {
    const images = await post('/api/v1/hosts/local/images/list', { pageSize: 100 })
    const items = (images.json.data as { items: { id: string; repoTags: string[] }[] }).items
    expect(items.length).toBeGreaterThan(0)

    for (const item of items) {
      const { status, json } = await post('/api/v1/hosts/local/images/inspect', {
        imageId: item.id
      })
      expect(status, `镜像 ${item.repoTags[0] ?? '<none>'} 在列表里但 inspect 不到`).toBe(200)
      expect(json.success).toBe(true)
    }
  })

  /**
   * 列表项与详情必须说同一件事。
   *
   * `ContainerSummary.state` 与 `ContainerDetail.state.status` 是同一条信息的两个投影，
   * 夹具一旦分两份手写就会漂移，症状是「列表显示运行中、点进去却是已停止」——
   * 界面上完全看不出哪个是真的，比直接报错难查得多。
   */
  it('列表的 state 与详情的 status 逐条一致', async () => {
    const list = await post('/api/v1/hosts/local/containers/list', { all: true, pageSize: 100 })
    const items = (list.json.data as { items: { id: string; name: string; state: string }[] }).items

    for (const item of items) {
      const { json } = await post('/api/v1/hosts/local/containers/inspect', { containerId: item.id })
      const detail = json.data as { state: { status: string } }

      expect(detail.state.status, `容器 ${item.name} 的列表与详情状态不一致`).toBe(item.state)
    }
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
    // 省略 role 时取**默认角色**（由角色自身的 isDefault 决定，当前为 auditor）
    expect((created.json.data as { roles: string[] }).roles).toEqual(['auditor'])

    // pageSize 100：新账号是追加到列表末尾的，列表超过一页时只看默认 20 条会看不到它。
    // 这正是真实后端的行为（新建的人排在最后），mock 不假装把他插到第一页。
    const list = await post('/api/v1/admin/users/list', { pageSize: 100 })
    expect(
      (list.json.data as { items: { username: string }[] }).items.some(
        (user) => user.username === 'deploy-bot'
      )
    ).toBe(true)
  })

  it('删除当前登录账号 → 1000 / 400（后端护栏）', async () => {
    // pageSize 100：按用户名找的人必须在**全量**里找，默认 20 条会漏掉排在前一页之后的账号
    const list = await post('/api/v1/admin/users/list', { pageSize: 100 })
    const self = (list.json.data as { items: { id: string; username: string }[] }).items.find(
      (user) => user.username === 'admin'
    )

    const { status, json } = await post('/api/v1/admin/users/delete', { userId: self!.id })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('删除最后一个管理员 → 1000 / 400', async () => {
    // 先把唯一的管理员之外没有其他 admin，因此删它必然命中「最后一个管理员」
    // ⚠️ 必须 pageSize 100：否则「只有一个管理员」这条断言会被分页悄悄放过
    // （真出现第二个管理员且排在第一页之后，用例照样绿）。
    const list = await post('/api/v1/admin/users/list', { pageSize: 100 })
    const admins = (
      list.json.data as { items: { id: string; roles: string[] }[] }
    ).items.filter((user) => user.roles.includes('admin'))

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

describe('批量删除端点（0.1.0）', () => {
  /** 后端上限 100，超出会是整批 1000 而不是逐条失败。 */
  const overLimit = Array.from({ length: 101 }, (_, index) => `c${index}`)

  it('入参为空 → 1000 / 400（整批失败，与逐条失败形态不同）', async () => {
    const containers = await post('/api/v1/hosts/local/containers/batch-remove', {
      containerIds: []
    })
    expect(containers.status).toBe(400)
    expect(containers.json.code).toBe(ErrorCode.ValidationFailed)

    const images = await post('/api/v1/hosts/local/images/batch-remove', { imageIds: [] })
    expect(images.status).toBe(400)
    expect(images.json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('列表全为空串 → 1000 / 400', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/batch-remove', {
      containerIds: ['  ', '', '\t']
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('超过 100 条 → 1000 / 400', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/batch-remove', {
      containerIds: overLimit
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
    expect(json.message).toContain('100')
  })

  it('重复 ID 按去重后的个数统计（否则界面会显示「成功 0、失败 2」）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/batch-remove', {
      containerIds: ['ghost-batch', 'ghost-batch', ' ghost-batch ']
    })

    expect(status).toBe(200)

    const result = json.data as { requested: number; succeeded: number }
    expect(result.requested).toBe(1)
    expect(result.succeeded).toBe(0)
  })

  it('部分成功也是 200，失败逐条带容器 ID 与错误码', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/batch-remove', {
      containerIds: ['ghost-a', 'ghost-b']
    })

    // 关键：全部失败**也**是 200。若这里是 4xx，界面就只能弹一个笼统的错误框
    expect(status).toBe(200)

    const result = json.data as {
      requested: number
      succeeded: number
      failures: { resourceId: string; code: number; message: string }[]
    }

    expect(result.requested).toBe(2)
    expect(result.succeeded).toBe(0)
    expect(result.failures).toHaveLength(2)
    expect(result.failures.map((failure) => failure.resourceId)).toEqual(['ghost-a', 'ghost-b'])
    // 错误码必须与单条端点一致（1401），界面才能复用同一套文案
    expect(result.failures.every((failure) => failure.code === ErrorCode.DockerResourceNotFound)).toBe(
      true
    )
  })

  it('不带 force 删除运行中的容器 → 该条失败 1404，其余照常成功', async () => {
    const list = await post('/api/v1/hosts/local/containers/list', { all: true })
    const items = (list.json.data as { items: { id: string; state: string }[] }).items
    const running = items.find((item) => item.state === 'running')
    const stopped = items.find((item) => item.state !== 'running')

    if (running === undefined || stopped === undefined) {
      // 夹具里没有「一运行一停止」的组合：前提不成立，跳过而不是假装通过
      return
    }

    const { status, json } = await post('/api/v1/hosts/local/containers/batch-remove', {
      containerIds: [running.id, stopped.id]
    })

    expect(status).toBe(200)

    const result = json.data as {
      succeeded: number
      failures: { resourceId: string; code: number }[]
    }

    // 关键：已停止的那个**必须真的删掉**，而不是因为同批里有一条失败就整批中止
    expect(result.succeeded).toBe(1)
    expect(result.failures).toHaveLength(1)
    expect(result.failures[0].resourceId).toBe(running.id)
    expect(result.failures[0].code).toBe(ErrorCode.DockerResourceConflict)

    const after = await post('/api/v1/hosts/local/containers/list', { all: true })
    expect((after.json.data as { items: { id: string }[] }).items.map((i) => i.id)).not.toContain(
      stopped.id
    )
  })

  it('镜像批量删除同样返回逐条失败', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/batch-remove', {
      imageIds: ['ghost-image:latest']
    })

    expect(status).toBe(200)

    const result = json.data as { requested: number; failures: { code: number }[] }
    expect(result.requested).toBe(1)
    expect(result.failures[0].code).toBe(ErrorCode.DockerResourceNotFound)
  })

  it('主机不存在 → 1300 / 404（整批失败）', async () => {
    const { status, json } = await post('/api/v1/hosts/nope/containers/batch-remove', {
      containerIds: ['anything']
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.HostNotFound)
  })
})

describe('角色与权限目录端点（0.1.0）', () => {
  const ADMIN_ROLE = '00000000-0000-0000-0000-0000000000a1'
  const AUDITOR_ROLE = '00000000-0000-0000-0000-0000000000a4'
  const DEPLOYER_ROLE = '00000000-0000-0000-0000-0000000000c1'

  it('角色列表返回**普通数组**（不是分页信封），字段与 RoleSummary 对齐', async () => {
    const { status, json } = await post('/api/v1/admin/roles/list')

    expect(status).toBe(200)
    expect(Array.isArray(json.data)).toBe(true)

    const roles = json.data as {
      id: string
      name: string
      displayName: string | null
      description: string | null
      isBuiltIn: boolean
      isDefault: boolean
      permissions: string[]
      assignmentCount: number
    }[]

    // 夹具里的五个角色（四个内置 + 一个自定义）
    expect(roles.map((role) => role.name)).toEqual([
      'admin',
      'operator',
      'viewer',
      'auditor',
      'deployer'
    ])

    const admin = roles.find((role) => role.name === 'admin')!
    expect(admin.isBuiltIn).toBe(true)
    // 0.4.0 把目录从 16 条扩到 18 条（registry.read / registry.write）。
    // admin 的权限集必须覆盖**整个目录**，因此这里跟着目录长度走：
    // 写死 16 会在新增权限时拦下「admin 漏了一项」——而那正是最该拦的情况。
    expect(admin.permissions).toHaveLength(18)
    expect(typeof admin.assignmentCount).toBe('number')

    // 全库至多一个默认角色
    expect(roles.filter((role) => role.isDefault).map((role) => role.name)).toEqual(['auditor'])
  })

  it('customOnly=true 只返回自定义角色', async () => {
    const { json } = await post('/api/v1/admin/roles/list', { customOnly: true })

    const roles = json.data as { name: string; isBuiltIn: boolean }[]
    expect(roles.map((role) => role.name)).toEqual(['deployer'])
    expect(roles.every((role) => !role.isBuiltIn)).toBe(true)
  })

  it('权限目录共 18 条，Host 域在前、Global 在后（矩阵的分组顺序就来自它）', async () => {
    const { status, json } = await post('/api/v1/admin/roles/permissions')

    expect(status).toBe(200)

    const permissions = json.data as {
      name: string
      domain: string
      level: string
      scope: string
    }[]

    expect(permissions).toHaveLength(18)
    expect(permissions[0].name).toBe('host.read')
    expect(permissions.at(-1)!.name).toBe('audit.read')
    // 作用域是判定依据：host.manage 与 host.read 前缀相同、作用域完全不同
    expect(permissions.find((item) => item.name === 'host.manage')!.scope).toBe('global')
    expect(permissions.find((item) => item.name === 'host.read')!.scope).toBe('host')

    // Registry 两项是**全局**作用域：同一份凭据在所有主机上通用，
    // 挂到主机下会让同一把口令在 N 台机器上各存一份、迟早漂移。
    for (const name of ['registry.read', 'registry.write']) {
      expect(permissions.find((item) => item.name === name)?.scope, name).toBe('global')
    }
  })

  it('角色名重复 → 1511 / 400（且不区分大小写）', async () => {
    const { status, json } = await post('/api/v1/admin/roles/create', {
      name: 'Deployer',
      displayName: '重复'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.RoleNameAlreadyExists)
  })

  it('未知权限名 → 1514 / 400，且不落库', async () => {
    const { status, json } = await post('/api/v1/admin/roles/create', {
      name: 'audit-only',
      displayName: '审计专用',
      permissions: ['root']
    })

    // ⚠️ 必须是 1514 而不是笼统的 1000：客户端最容易犯的错就是拼错权限名
    // （"read" 而不是 "host.read"、"ALL"、"7"），给它独立码，
    // 界面才能提示「请从权限目录里选」。见 docs/api-spec.md「未知权限名一律 1514」。
    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.PermissionUnknown)

    // 校验必须在写库之前：非法输入不允许留下「待修正」的角色
    const after = await post('/api/v1/admin/roles/list')
    expect((after.json.data as { name: string }[]).some((role) => role.name === 'audit-only')).toBe(
      false
    )
  })

  it('空串权限标识 → 1000 / 400（参数本身不合法，与「名字拼错」分开）', async () => {
    const { status, json } = await post('/api/v1/admin/roles/create', {
      name: 'blankperm',
      permissions: ['  ']
    })

    // 1000 = 参数不合法；1514 = 名字不在目录里。两者语义不同，界面提示也不同
    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('新建的角色立即出现在列表里（否则界面会「创建成功但看不到」）', async () => {
    const created = await post('/api/v1/admin/roles/create', {
      name: 'maintainer',
      displayName: '维护',
      permissions: ['host.read', 'container.read']
    })

    expect(created.status).toBe(200)

    const { json } = await post('/api/v1/admin/roles/list')
    const names = (json.data as { name: string }[]).map((role) => role.name)
    expect(names).toContain('maintainer')
  })

  it('内置角色的可读信息**可以**改（受保护的是权限集与删除）', async () => {
    const { status } = await post('/api/v1/admin/roles/update', {
      roleId: AUDITOR_ROLE,
      displayName: '审计员',
      description: '只读 + 审计'
    })

    expect(status).toBe(200)
  })

  it('内置角色的权限集不可改 → 1513 / 400', async () => {
    const { status, json } = await post('/api/v1/admin/roles/set-permissions', {
      roleId: AUDITOR_ROLE,
      permissions: ['audit.read']
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.RoleProtected)
  })

  it('内置角色不可删 → 1513 / 400', async () => {
    const { status, json } = await post('/api/v1/admin/roles/remove', { roleId: ADMIN_ROLE })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.RoleProtected)
  })

  it('仍有有效分配的角色不可删 → 1512 / 400（不级联撤销）', async () => {
    // ⚠️ 必须用**自定义**角色：内置角色在权限集 / 删除上先被 1513 拦住，
    // 用内置角色测 1512 永远看不到那条分支。deployer 夹具里被 deployer-bot 全局持有。
    const { status, json } = await post('/api/v1/admin/roles/remove', {
      roleId: DEPLOYER_ROLE
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.RoleInUse)
  })

  it('自定义角色在分配撤销后可以删除', async () => {
    // deployer 夹具里有多个人全局持有 → 必须**逐个撤销全部持有者**才能删掉角色。
    // 只撤一个就删，走到的仍是 1512（仍有分配）那条分支，本用例就白写了。
    const users = await post('/api/v1/admin/users/list', { pageSize: 100 })
    const holders = (users.json.data as { items: { id: string; roles: string[] }[] }).items.filter(
      (user) => user.roles.includes('deployer')
    )
    expect(holders.length).toBeGreaterThan(1)

    for (const holder of holders) {
      await post(`/api/v1/admin/users/${holder.id}/roles/revoke`, {
        roleId: DEPLOYER_ROLE,
        host: null
      })
    }

    const { status } = await post('/api/v1/admin/roles/remove', { roleId: DEPLOYER_ROLE })
    expect(status).toBe(200)

    const after = await post('/api/v1/admin/roles/list')
    expect((after.json.data as { name: string }[]).some((role) => role.name === 'deployer')).toBe(
      false
    )
  })

  it('set-permissions 是覆盖语义（没给的不再保留）', async () => {
    await post('/api/v1/admin/roles/create', {
      name: 'ops-lite',
      displayName: '轻运维',
      permissions: ['host.read', 'container.read']
    })

    const before = await post('/api/v1/admin/roles/list')
    const target = (before.json.data as { id: string; name: string }[]).find(
      (role) => role.name === 'ops-lite'
    )!

    await post('/api/v1/admin/roles/set-permissions', {
      roleId: target.id,
      permissions: ['image.read']
    })

    const after = await post('/api/v1/admin/roles/list')
    const updated = (after.json.data as { id: string; permissions: string[] }[]).find(
      (role) => role.id === target.id
    )!

    expect(updated.permissions).toEqual(['image.read'])
  })

  it('set-default 保证全库至多一个默认角色', async () => {
    await post('/api/v1/admin/roles/set-default', { roleId: DEPLOYER_ROLE })

    const { json } = await post('/api/v1/admin/roles/list')
    const defaults = (json.data as { name: string; isDefault: boolean }[]).filter(
      (role) => role.isDefault
    )

    expect(defaults.map((role) => role.name)).toEqual(['deployer'])
  })

  it('角色不存在 → 1510 / 404', async () => {
    const { status, json } = await post('/api/v1/admin/roles/set-permissions', {
      roleId: '00000000-0000-0000-0000-0000000000ff',
      permissions: ['host.read']
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.RoleNotFound)
  })

  it('既没给 roleId 也没给 role → 1000 / 400（定位条件缺失与「不存在」不同）', async () => {
    const { status, json } = await post('/api/v1/admin/roles/set-permissions', {
      permissions: ['host.read']
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })
})

describe('角色分配端点（0.1.0）', () => {
  const OPERATOR_ROLE = '00000000-0000-0000-0000-0000000000a2'
  const VIEWER_ROLE = '00000000-0000-0000-0000-0000000000a3'
  const OPS = '00000000-0000-0000-0000-0000000000b2'
  const LI = '00000000-0000-0000-0000-0000000000b3'

  it('用户视角的 list 同时带出全局分配与主机分配', async () => {
    const { status, json } = await post(`/api/v1/admin/users/${OPS}/roles/list`)

    expect(status).toBe(200)

    const assignments = json.data as {
      userId: string
      role: string
      hostId: string | null
      hostName: string | null
    }[]

    // ops-zhang：全局 operator + staging-01 上的 viewer
    expect(assignments).toHaveLength(2)
    expect(assignments.some((item) => item.hostId === null && item.hostName === null)).toBe(true)
    expect(assignments.some((item) => item.hostName === 'staging-01')).toBe(true)
  })

  it('主机视角的 list **不含全局分配**（它对所有主机生效、无法归属到某一台）', async () => {
    const { status, json } = await post('/api/v1/admin/hosts/prod-01/roles/list')

    expect(status).toBe(200)

    const assignments = json.data as { username: string; hostName: string }[]

    // prod-01 上只有 readonly-li 的 viewer；ops-zhang 的分配在 staging-01 上
    expect(assignments.map((item) => item.username)).toEqual(['readonly-li'])
    expect(assignments[0].hostName).toBe('prod-01')
  })

  it('主机视角的 list 带 userId 即按人过滤；用户不存在 → 1201 / 404', async () => {
    const filtered = await post('/api/v1/admin/hosts/staging-01/roles/list', { userId: OPS })
    expect((filtered.json.data as unknown[])).toHaveLength(1)

    const missing = await post('/api/v1/admin/hosts/staging-01/roles/list', {
      userId: '00000000-0000-0000-0000-999999999999'
    })

    expect(missing.status).toBe(404)
    expect(missing.json.code).toBe(ErrorCode.UserNotFound)
  })

  it('主机不存在 → 1300 / 404', async () => {
    const { status, json } = await post('/api/v1/admin/hosts/nope/roles/list')

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.HostNotFound)
  })

  it('分配是幂等的：重复分配返回同一条记录（主键不变）', async () => {
    const first = await post(`/api/v1/admin/hosts/prod-01/roles/assign`, {
      userId: LI,
      roleId: OPERATOR_ROLE
    })
    const second = await post(`/api/v1/admin/hosts/prod-01/roles/assign`, {
      userId: LI,
      roleId: OPERATOR_ROLE
    })

    expect(first.status).toBe(200)
    expect((first.json.data as { id: string }).id).toBe((second.json.data as { id: string }).id)
  })

  it('同一用户在同一台主机上可以持有多个角色（有效权限取并集）', async () => {
    // readonly-li 夹具里已在 prod-01 上是 viewer，再补一个 operator
    await post(`/api/v1/admin/hosts/prod-01/roles/assign`, {
      userId: LI,
      roleId: OPERATOR_ROLE
    })

    const { json } = await post('/api/v1/admin/hosts/prod-01/roles/list', { userId: LI })
    const assignments = json.data as { role: string }[]

    expect(assignments.map((item) => item.role).sort()).toEqual(['operator', 'viewer'])
  })

  it('用户视角的 assign 省略 host 即全局分配（与主机分配严格区分）', async () => {
    await post(`/api/v1/admin/users/${LI}/roles/assign`, { roleId: VIEWER_ROLE })

    const { json } = await post(`/api/v1/admin/users/${LI}/roles/list`)
    const assignments = json.data as { roleId: string; hostId: string | null }[]

    const global = assignments.filter((item) => item.hostId === null)
    expect(global.map((item) => item.roleId)).toContain(VIEWER_ROLE)
  })

  it('撤销不存在的分配 → 1515 / 404', async () => {
    const { status, json } = await post(`/api/v1/admin/users/${LI}/roles/revoke`, {
      roleId: OPERATOR_ROLE,
      host: null
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.RoleAssignmentNotFound)
  })

  it('撤销走软删除：撤销后可以重新分配同一组合', async () => {
    const revoke = await post(`/api/v1/admin/users/${OPS}/roles/revoke`, {
      roleId: OPERATOR_ROLE,
      host: null
    })
    expect(revoke.status).toBe(200)

    const reassign = await post(`/api/v1/admin/users/${OPS}/roles/assign`, {
      roleId: OPERATOR_ROLE
    })
    expect(reassign.status).toBe(200)

    const { json } = await post(`/api/v1/admin/users/${OPS}/roles/list`)
    expect(
      (json.data as { roleId: string; hostId: string | null }[]).some(
        (item) => item.roleId === OPERATOR_ROLE && item.hostId === null
      )
    ).toBe(true)
  })

  it('角色不存在 → 1510 / 404（分配端点也要报对错误码）', async () => {
    const { status, json } = await post('/api/v1/admin/hosts/prod-01/roles/assign', {
      userId: LI,
      roleId: '00000000-0000-0000-0000-0000000000ff'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.RoleNotFound)
  })
})

/* ==================================================================
 * 0.3.0：容器基础操作与镜像打标签
 *
 * 这些用例的价值全在「mock 复刻了哪些**容易被忽略**的语义」——
 * 与 0.1.0 那批批量端点的用例同源。不复刻的话，
 * 对应的界面分支在 mock 模式下永远走不到，而走查时看不出问题，
 * 到真机上才发现交互是错的。
 * ================================================================== */

describe('容器重命名（0.3.0）', () => {
  const CONTAINER = MOCK_CONTAINERS_DEFAULT[0].id

  it('重命名后详情能读到新名字', async () => {
    const renamed = await post('/api/v1/hosts/local/containers/rename', {
      containerId: CONTAINER,
      name: 'renamed-by-test'
    })

    expect(renamed.status).toBe(200)
    expect((renamed.json.data as { name: string }).name).toBe('renamed-by-test')

    const detail = await post('/api/v1/hosts/local/containers/inspect', { containerId: CONTAINER })
    expect((detail.json.data as { name: string }).name).toBe('renamed-by-test')

    const list = await post('/api/v1/hosts/local/containers/list', { all: true })
    const items = (list.json.data as { items: Array<{ id: string; name: string }> }).items
    expect(items.find((item) => item.id === CONTAINER)?.name).toBe('renamed-by-test')
  })

  it('非法名称 → 1000 / 400（规则与创建一致）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/rename', {
      containerId: CONTAINER,
      name: '-不能以横杠开头'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('名称已被占用 → 1404 / 409（不是 1000）', async () => {
    const other = MOCK_CONTAINERS_DEFAULT[1]

    const { status, json } = await post('/api/v1/hosts/local/containers/rename', {
      containerId: CONTAINER,
      name: other.name
    })

    // 409 而不是 400：改一下名字就能成功，属「换个名字」而不是「参数错了」
    expect(status).toBe(409)
    expect(json.code).toBe(ErrorCode.DockerResourceConflict)
  })

  it('容器不存在 → 1401 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/rename', {
      containerId: 'no-such-container',
      name: 'whatever'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.DockerResourceNotFound)
  })
})

describe('容器热更新（0.3.0）', () => {
  const CONTAINER = MOCK_CONTAINERS_DEFAULT[0].id

  it('三个字段全省略 → 1000（那等于什么都不改）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/update', {
      containerId: CONTAINER
    })

    // 不模拟它的话，「什么都没填就点保存」在 mock 下会显示成功
    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('memoryLimitBytes 为 0 → 1000（Docker 会返回成功但不生效）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/update', {
      containerId: CONTAINER,
      memoryLimitBytes: 0
    })

    // 实测 `docker update --memory=0` 返回 200 而限制没变。
    // 放行 0 等于给用户一个「保存成功却什么也没做」的控件。
    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('nanoCpus 为 0 → 1000（同上）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/update', {
      containerId: CONTAINER,
      nanoCpus: 0
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('内存低于 6 MiB → 1000（守护进程硬性下限）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/update', {
      containerId: CONTAINER,
      memoryLimitBytes: 1024
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('非法重启策略 → 1000', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/update', {
      containerId: CONTAINER,
      restartPolicy: 'sometimes'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('回带更新后实际生效的值', async () => {
    const updated = await post('/api/v1/hosts/local/containers/update', {
      containerId: CONTAINER,
      restartPolicy: 'unless-stopped',
      memoryLimitBytes: 64 * 1024 * 1024
    })

    expect(updated.status).toBe(200)

    const data = updated.json.data as {
      restartPolicy: string
      memoryLimitBytes: number | null
      nanoCpus: number | null
    }

    expect(data.restartPolicy).toBe('unless-stopped')
    expect(data.memoryLimitBytes).toBe(64 * 1024 * 1024)
  })

  it('只改重启策略时内存上限保持不变（回归防线）', async () => {
    const first = MOCK_CONTAINERS_DEFAULT[2].id

    await post('/api/v1/hosts/local/containers/update', {
      containerId: first,
      memoryLimitBytes: 64 * 1024 * 1024
    })

    const second = await post('/api/v1/hosts/local/containers/update', {
      containerId: first,
      restartPolicy: 'always'
    })

    // Docker 的 update 对「未传字段」的处理随版本变化（老版本写回 0）；
    // 后端先 inspect 再按当前值回填，mock 必须复刻这一点。
    expect((second.json.data as { memoryLimitBytes: number | null }).memoryLimitBytes).toBe(
      64 * 1024 * 1024
    )
  })
})

describe('容器复制（0.3.0）', () => {
  const CONTAINER = MOCK_CONTAINERS_DEFAULT[0].id

  it('默认名是「原名 + -copy」，且 recreated 为 false', async () => {
    const created = await post('/api/v1/hosts/local/containers/duplicate', {
      containerId: CONTAINER
    })

    expect(created.status).toBe(200)

    const data = created.json.data as {
      name: string
      replacedContainerId: string | null
      recreated: boolean
    }

    expect(data.name).toBe(`${MOCK_CONTAINERS_DEFAULT[0].name}-copy`)
    // ⚠️ 复制不是重建：原容器毫发无损，界面据此把提示写成「已复制」
    expect(data.recreated).toBe(false)
    expect(data.replacedContainerId).toBeNull()
  })

  it('连点两次不失败（名字自动递增 -2）', async () => {
    const source = MOCK_CONTAINERS_DEFAULT[1].id
    const baseName = `${MOCK_CONTAINERS_DEFAULT[1].name}-copy`

    await post('/api/v1/hosts/local/containers/duplicate', { containerId: source })

    const second = await post('/api/v1/hosts/local/containers/duplicate', {
      containerId: source
    })

    // 不避让的话第二次会拿到 409，而连点两次是复制最常见的用法
    expect(second.status).toBe(200)
    expect((second.json.data as { name: string }).name).toBe(`${baseName}-2`)
  })

  it('原容器仍然存在', async () => {
    const created = await post('/api/v1/hosts/local/containers/duplicate', {
      containerId: CONTAINER
    })
    const copyId = (created.json.data as { containerId: string }).containerId

    const original = await post('/api/v1/hosts/local/containers/inspect', { containerId: CONTAINER })
    const copy = await post('/api/v1/hosts/local/containers/inspect', { containerId: copyId })

    expect(original.status).toBe(200)
    expect(copy.status).toBe(200)
    expect((original.json.data as { id: string }).id).not.toBe(
      (copy.json.data as { id: string }).id
    )
  })

  it('容器不存在 → 1401 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/duplicate', {
      containerId: 'no-such-container'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.DockerResourceNotFound)
  })
})

describe('容器追加挂载（0.3.0）', () => {
  it('重建后 ID 改变，且回带被让位的旧容器', async () => {
    const source = MOCK_CONTAINERS_DEFAULT[0].id

    const recreated = await post('/api/v1/hosts/local/containers/add-volumes', {
      containerId: source,
      volumes: [{ source: '/tmp', destination: '/mnt/extra' }],
      start: false
    })

    expect(recreated.status).toBe(200)

    const data = recreated.json.data as {
      containerId: string
      replacedContainerId: string | null
      recreated: boolean
      running: boolean
    }

    // ⚠️ recreated=true 且 ID 变化：这是「会重建」的可观察证据
    expect(data.recreated).toBe(true)
    expect(data.containerId).not.toBe(source)
    expect(data.replacedContainerId).toBe(source)
    expect(data.running).toBe(false)
  })

  it('新容器多出该挂载，旧容器改名保留', async () => {
    const source = MOCK_CONTAINERS_DEFAULT[1].id

    const recreated = await post('/api/v1/hosts/local/containers/add-volumes', {
      containerId: source,
      volumes: [{ source: '/tmp', destination: '/mnt/added' }],
      start: false
    })

    const newId = (recreated.json.data as { containerId: string }).containerId

    const detail = await post('/api/v1/hosts/local/containers/inspect', { containerId: newId })
    const mounts = (detail.json.data as { mounts: Array<{ destination: string }> }).mounts
    expect(mounts.map((mount) => mount.destination)).toContain('/mnt/added')

    // 旧容器仍在但改了名——那是回滚的后手，界面必须提示用户可以删
    const parked = await post('/api/v1/hosts/local/containers/inspect', { containerId: source })
    expect((parked.json.data as { name: string }).name).toContain('whalefleet-replaced-')
  })

  it('start 省略时沿用原容器的运行状态', async () => {
    const running = MOCK_CONTAINERS_DEFAULT.find((item) => item.state === 'running')!

    const recreated = await post('/api/v1/hosts/local/containers/add-volumes', {
      containerId: running.id,
      volumes: [{ source: '/tmp', destination: '/mnt/keep-state' }]
    })

    // 原本在运行 → 重建后仍在运行；擅自启动一个本来没运行的容器是用户没要求的副作用
    expect((recreated.json.data as { running: boolean }).running).toBe(true)
  })

  it('挂载列表为空 → 1000（一次没有任何收益的重建）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/add-volumes', {
      containerId: MOCK_CONTAINERS_DEFAULT[0].id,
      volumes: []
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('挂载项缺目标路径 → 1000', async () => {
    const { status, json } = await post('/api/v1/hosts/local/containers/add-volumes', {
      containerId: MOCK_CONTAINERS_DEFAULT[0].id,
      volumes: [{ source: '/tmp' }]
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })
})

describe('镜像打标签（0.3.0）', () => {
  const IMAGE = MOCK_IMAGES_DEFAULT.find((image) => image.repoTags.length > 0)!

  it('新标签与源标签同属一个镜像 ID（tag 不复制镜像）', async () => {
    const tagged = await post('/api/v1/hosts/local/images/tag', {
      imageId: IMAGE.id,
      repository: 'whalefleet-test/tag',
      tag: 'v1'
    })

    expect(tagged.status).toBe(200)

    const data = tagged.json.data as { imageId: string; reference: string; repoTags: string[] }

    // ⚠️ 核心语义：tag 只是加一个引用，磁盘占用不变。
    // 界面据此把行更新成「同一镜像的另一个标签」而不是「多了一行镜像」。
    expect(data.imageId).toBe(IMAGE.id)
    expect(data.reference).toBe('whalefleet-test/tag:v1')
    expect(data.repoTags).toContain('whalefleet-test/tag:v1')

    const before = MOCK_IMAGES_DEFAULT.filter((image) => image.id === IMAGE.id).length
    const list = await post('/api/v1/hosts/local/images/list', {})
    const after = (list.json.data as { items: Array<{ id: string }> }).items.filter(
      (image) => image.id === IMAGE.id
    ).length

    // 镜像**条数**没变：多标签属于同一个镜像
    expect(after).toBe(before)
  })

  it('列表与详情的标签一致（两处不能对不上）', async () => {
    const reference = 'whalefleet-test/consistent:v2'

    await post('/api/v1/hosts/local/images/tag', {
      imageId: IMAGE.id,
      repository: 'whalefleet-test/consistent',
      tag: 'v2'
    })

    const detail = await post('/api/v1/hosts/local/images/inspect', { imageId: IMAGE.id })
    expect((detail.json.data as { repoTags: string[] }).repoTags).toContain(reference)

    const list = await post('/api/v1/hosts/local/images/list', {})
    const items = (list.json.data as { items: Array<{ id: string; repoTags: string[] }> }).items
    expect(items.find((image) => image.id === IMAGE.id)?.repoTags).toContain(reference)
  })

  it('repository 含标签 → 1000（而不是拼成 nginx:1.27:alpine）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/tag', {
      imageId: IMAGE.id,
      repository: 'nginx:1.27',
      tag: 'alpine'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('标签非法 → 1000（数字开头 / 含斜杠 / 含冒号）', async () => {
    for (const tag of ['1.27', 'team/app', 'a:b']) {
      const { status, json } = await post('/api/v1/hosts/local/images/tag', {
        imageId: IMAGE.id,
        repository: 'whalefleet-test/bad',
        tag
      })

      expect(status).toBe(400)
      expect(json.code).toBe(ErrorCode.ValidationFailed)
    }
  })

  it('tag 省略或空串都取 latest（与 pull 端点一致）', async () => {
    for (const tag of [undefined, '']) {
      const tagged = await post('/api/v1/hosts/local/images/tag', {
        imageId: IMAGE.id,
        repository: 'whalefleet-test',
        tag
      })

      expect((tagged.json.data as { reference: string }).reference).toBe('whalefleet-test:latest')
    }
  })

  it('镜像不存在 → 1401 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/local/images/tag', {
      imageId: 'no-such-image-0123456789',
      repository: 'whalefleet-test/tag',
      tag: 'v1'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.DockerResourceNotFound)
  })
})
