/**
 * @vitest-environment node
 *
 * 刻意用 **node 环境**（同 `mock-contract.spec.ts`）：
 * jsdom 自带的 `AbortSignal` 与 Node 原生 `fetch` 不是同一个实例，
 * MSW 的 fetch 拦截器会因此抛
 * `RequestInit: Expected signal ("AbortSignal {}") to be an instance of AbortSignal`。
 *
 * 本文件只验证 HTTP 契约，完全不碰 DOM。
 */
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { mockServer } from '../src/mocks/node'
import { setMockScenario } from '../src/mocks/index'
import { resetSessionStacks } from '../src/mocks/handlers/stacks'
import type { StackListResult, StackTaskSnapshot } from '../src/types/api'

/**
 * 编排 Stack 端点的 mock 契约测试。
 *
 * ## 重点验的是「容易忽略的语义」
 * 1. **部署返回 202**：不是 200。前端若把 202 当失败，会把「已受理」报成「部署失败」。
 * 2. **同名部署报 1902**、**更新不存在的报 1900**：方向相反的两条。
 * 3. **YAML 非法报 1904 并带出字段路径**。
 * 4. **任务状态随时间推进**：`queued → running → succeeded`——
 *    永远返回 `queued` 的话「完成后刷新列表」的分支永远测不到。
 * 5. **Webhook 令牌明文只给一次**。
 * 6. **令牌无效一律 404**：不区分「不存在 / 已吊销 / 未启用」。
 */
beforeAll(() => mockServer.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  mockServer.resetHandlers()
  setMockScenario('default')
  resetSessionStacks()
})
afterAll(() => mockServer.close())

const BASE = 'http://localhost:5173'

async function post(path: string, body?: unknown) {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  })
  return { status: res.status, json: (await res.json()) as Record<string, unknown> }
}

const VALID_COMPOSE = `services:
  app:
    image: alpine:3.20
    command: ["/bin/sh", "-c", "sleep 300"]
`

describe('编排 Stack 列表', () => {
  it('返回夹具里的编排，含受管与不受管两类', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/list', {})

    expect(status).toBe(200)
    expect(json.success).toBe(true)

    const data = json.data as StackListResult
    expect(data.items.length).toBeGreaterThan(0)

    // 不受管的外部项目必须在列表里——用户需要知道它存在且「不能更新」
    const external = data.items.find((item) => item.name === 'external')
    expect(external?.managed).toBe(false)
    expect(external?.composeAvailable).toBe(false)
  })

  it('搜索在服务端过滤且分页随之收敛', async () => {
    const { json } = await post('/api/v1/hosts/local/stacks/list', { search: 'blo' })
    const data = json.data as StackListResult

    expect(data.items).toHaveLength(1)
    expect(data.items[0]?.name).toBe('blog')
    // ⚠️ total 必须是过滤**后**的条数，否则页脚显示的数字与翻页结果对不上
    expect(data.total).toBe(1)
  })

  it('空搜索返回全部（不是空列表）', async () => {
    const { json } = await post('/api/v1/hosts/local/stacks/list', { search: '' })
    const data = json.data as StackListResult

    expect(data.items.length).toBeGreaterThan(1)
  })

  it('empty 场景返回空列表', async () => {
    setMockScenario('empty')
    const { json } = await post('/api/v1/hosts/local/stacks/list', {})

    expect((json.data as StackListResult).items).toHaveLength(0)
  })
})

describe('编排 Stack 详情', () => {
  it('受管项目给出 YAML 原文与服务列表', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/inspect', { name: 'shop' })

    expect(status).toBe(200)

    const data = json.data as { composeContent: string | null; services: unknown[] }
    expect(data.composeContent).toContain('services:')
    expect(data.services.length).toBe(2)
  })

  it('不受管项目的 composeContent 为 null', async () => {
    const { json } = await post('/api/v1/hosts/local/stacks/inspect', { name: 'external' })

    expect((json.data as { composeContent: string | null }).composeContent).toBeNull()
  })

  it('不存在的编排报 1900 / 404', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/inspect', { name: 'nope' })

    expect(status).toBe(404)
    expect(json.code).toBe(1900)
  })
})

describe('编排 Stack 部署', () => {
  it('返回 202 而不是 200（部署是异步的）', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/deploy', {
      name: 'demo',
      composeContent: VALID_COMPOSE,
      taskId: 'task-token-0000000000000000000000000000'
    })

    expect(status).toBe(202)
    expect(json.success).toBe(true)
    expect((json.data as { taskId: string }).taskId).toBe(
      'task-token-0000000000000000000000000000'
    )
  })

  it('同名部署报 1902 / 400', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/deploy', {
      name: 'shop',
      composeContent: VALID_COMPOSE,
      taskId: 'task-token-0000000000000000000000000001'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(1902)
  })

  it('YAML 含不支持的字段报 1904 并带出字段路径', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/deploy', {
      name: 'demo2',
      composeContent: 'services:\n  app:\n    image: alpine:3.20\n    build: .\n',
      taskId: 'task-token-0000000000000000000000000002'
    })

    expect(status).toBe(400)
    expect(json.code).toBe(1904)
    expect(String(json.message)).toContain('services.app.build')
  })

  it('部署后列表里能看到它（mock 必须有记忆）', async () => {
    await post('/api/v1/hosts/local/stacks/deploy', {
      name: 'brand-new',
      composeContent: VALID_COMPOSE,
      taskId: 'task-token-0000000000000000000000000003'
    })

    const { json } = await post('/api/v1/hosts/local/stacks/list', {})
    const data = json.data as StackListResult

    expect(data.items.some((item) => item.name === 'brand-new')).toBe(true)
  })
})

describe('编排 Stack 更新', () => {
  it('更新不存在的编排报 1900', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/update', {
      name: 'nope',
      composeContent: VALID_COMPOSE,
      taskId: 'task-token-0000000000000000000000000010'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(1900)
  })

  it('更新不受管的外部项目报 1903 / 409', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/update', {
      name: 'external',
      composeContent: VALID_COMPOSE,
      taskId: 'task-token-0000000000000000000000000011'
    })

    expect(status).toBe(409)
    expect(json.code).toBe(1903)
  })

  it('更新受管项目返回 202', async () => {
    const { status } = await post('/api/v1/hosts/local/stacks/update', {
      name: 'shop',
      composeContent: VALID_COMPOSE,
      taskId: 'task-token-0000000000000000000000000012'
    })

    expect(status).toBe(202)
  })
})

describe('编排 Stack 任务', () => {
  it('任务状态随时间推进到 succeeded', async () => {
    const taskId = 'task-token-0000000000000000000000000020'

    await post('/api/v1/hosts/local/stacks/deploy', {
      name: 'progress-demo',
      composeContent: VALID_COMPOSE,
      taskId
    })

    // 刚入队：queued
    const first = await post('/api/v1/hosts/local/stacks/task', { taskId })
    expect((first.json.data as StackTaskSnapshot).status).toBe('queued')

    // 等 mock 的任务时长过去（1.2 秒）
    await new Promise((resolve) => setTimeout(resolve, 1400))

    const second = await post('/api/v1/hosts/local/stacks/task', { taskId })
    const snapshot = second.json.data as StackTaskSnapshot

    expect(snapshot.status).toBe('succeeded')
    expect(snapshot.percent).toBe(100)
    expect(snapshot.result).not.toBeNull()
  })

  it('查询不存在的任务返回 null 而不是报错', async () => {
    const { status, json } = await post('/api/v1/hosts/local/stacks/task', {
      taskId: 'task-token-does-not-exist-0000000000'
    })

    expect(status).toBe(200)
    expect(json.data).toBeNull()
  })
})

describe('编排 Stack Webhook', () => {
  it('轮换令牌时给出明文，之后只能看到提示', async () => {
    const { json } = await post('/api/v1/hosts/local/stacks/webhook/rotate', { name: 'shop' })
    const info = json.data as { token: string | null; tokenHint: string | null }

    expect(info.token).not.toBeNull()
    expect(info.tokenHint).not.toBeNull()
    expect(String(info.token).startsWith(String(info.tokenHint))).toBe(true)
  })

  it('吊销后令牌失效', async () => {
    await post('/api/v1/hosts/local/stacks/webhook/revoke', { name: 'shop' })

    const { json } = await post('/api/v1/hosts/local/stacks/list', {})
    const data = json.data as StackListResult
    const shop = data.items.find((item) => item.name === 'shop')

    expect(shop?.webhookEnabled).toBe(false)
    expect(shop?.webhookTokenHint).toBeNull()
  })

  it('无效令牌触发报 1906 / 404', async () => {
    const { status, json } = await post('/api/v1/webhooks/stacks/not-a-real-token', {})

    expect(status).toBe(404)
    expect(json.code).toBe(1906)
  })

  it('有效令牌触发返回 202', async () => {
    const rotate = await post('/api/v1/hosts/local/stacks/webhook/rotate', { name: 'blog' })
    const token = (rotate.json.data as { token: string }).token

    const { status, json } = await post(`/api/v1/webhooks/stacks/${token}`, {})

    expect(status).toBe(202)
    expect((json.data as { taskId: string }).taskId).toBeTruthy()
  })
})
