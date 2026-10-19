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
import { resetSessionResources } from '../src/mocks/handlers/resources'
import type {
  BatchOperationResult,
  NetworkDetail,
  NetworkListResult,
  VolumeListResult
} from '@/types/api'

/**
 * 数据卷与网络的 mock 契约守卫（0.2.0）。
 *
 * ## 守的是什么
 * 这些「后端有、mock 没复刻」的语义，每一条都会让前端分支测不到：
 * 1. **同名建卷成功 / 同名建网 409**——方向相反，照抄任一边都会错一条。
 * 2. **卷的 `force` 删不掉被引用的卷**——只有「卷不存在」时才变成功。
 * 3. **网络没有 `force`**，且内置网络禁删。
 * 4. **`size` / `refCount` 为 0** 与 **列表没有 `containerCount`**——
 *    mock 若补上这两个字段，前端就会写出后端永远不会给的数据。
 * 5. **网段格式非法 → 1000**（不是 1402/500）。
 */
/** MSW 的处理器路径不含主机名，发请求时必须给绝对地址（同 `mock-contract.spec.ts`）。 */
const BASE = 'http://localhost:5173'
const HOST = `${BASE}/api/v1/hosts/prod-01`

beforeAll(() => mockServer.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  mockServer.resetHandlers()
  setMockScenario('default')
  resetSessionResources()
})
afterAll(() => mockServer.close())

async function post(path: string, body: unknown): Promise<Response> {
  return fetch(`${HOST}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  })
}

async function readJson<T>(response: Response): Promise<T> {
  return (await response.json()) as T
}

/* ==================== 数据卷 ==================== */

describe('数据卷 mock 契约', () => {
  it('列表恒带 warnings 数组', async () => {
    const body = await readJson<{ data: VolumeListResult }>(await post('/volumes/list', {}))

    expect(body.data.warnings).toBeInstanceOf(Array)
    expect(body.data.items.length).toBeGreaterThan(0)
  })

  it('用量缺失时给 0 而不是省略字段', async () => {
    const body = await readJson<{ data: VolumeListResult }>(await post('/volumes/list', {}))

    // 前端靠 `size === 0` 显示「—」。若 mock 省略字段，JSON 里没有这个键，
    // 反序列化也会得到 undefined，判断分支就与真实后端不一致了。
    const withoutUsage = body.data.items.find((item) => item.size === 0)
    expect(withoutUsage, '夹具里应有一条用量缺失的卷').toBeDefined()

    // 两个字段恒为数值（0 而非 undefined）——refCount 与 size 相互独立：
    // 「不知道多大」和「没人引用」是两回事，不能互相推导
    expect(typeof withoutUsage?.size).toBe('number')
    expect(typeof withoutUsage?.refCount).toBe('number')

    // 而「没有任何容器引用」的卷 refCount 确实是 0
    const unused = body.data.items.find((item) => item.name === 'build-cache')
    expect(unused?.refCount).toBe(0)
    expect(unused?.size).toBeGreaterThan(0)
  })

  it('同名创建返回已存在的那一个而非报错', async () => {
    const first = await readJson<{ success: boolean; data: { name: string } }>(
      await post('/volumes/create', { name: 'same-name' })
    )
    const second = await post('/volumes/create', { name: 'same-name' })
    const body = await readJson<{ success: boolean; data: { name: string } }>(second)

    expect(first.data.name).toBe('same-name')
    // ⚠️ 网络那边同名是 409——这两条方向相反，是 Docker 本身的差异
    expect(second.status).toBe(200)
    expect(body.success).toBe(true)
  })

  it('卷名非法返回 1000', async () => {
    const response = await post('/volumes/create', { name: '-bad start' })
    const body = await readJson<{ code: number }>(response)

    expect(response.status).toBe(400)
    expect(body.code).toBe(ErrorCode.ValidationFailed)
  })

  it('仍被引用的卷带不带 force 都删不掉', async () => {
    // db-data 的 refCount 是 2
    const plain = await post('/volumes/remove', { name: 'db-data' })
    const forced = await post('/volumes/remove', { name: 'db-data', force: true })

    expect(plain.status).toBe(409)
    expect(forced.status).toBe(409)
  })

  it('force 只让「不存在」变成成功', async () => {
    const plain = await post('/volumes/remove', { name: 'ghost-volume' })
    const forced = await post('/volumes/remove', { name: 'ghost-volume', force: true })

    expect(plain.status).toBe(404)
    expect(forced.status).toBe(200)
  })

  it('批量删除重复项按去重后计数', async () => {
    const response = await post('/volumes/batch-remove', {
      names: ['build-cache', 'build-cache', ' build-cache ']
    })
    const body = await readJson<{ data: BatchOperationResult }>(response)

    // 后端 NormalizeIds 会去重：`requested` 必须是 1 而不是 3
    expect(body.data.requested).toBe(1)
    expect(body.data.succeeded).toBe(1)
  })

  it('批量删除部分成功仍是 200 并逐条汇报', async () => {
    const response = await post('/volumes/batch-remove', {
      names: ['build-cache', 'db-data']
    })
    const body = await readJson<{ data: BatchOperationResult }>(response)

    expect(response.status).toBe(200)
    expect(body.data.succeeded).toBe(1)
    expect(body.data.failures).toHaveLength(1)
    expect(body.data.failures[0].resourceId).toBe('db-data')
    // 错误码必须与单条端点一致，界面才能复用同一套文案
    expect(body.data.failures[0].code).toBe(ErrorCode.DockerResourceConflict)
  })

  it('空列表返回 1000', async () => {
    const response = await post('/volumes/batch-remove', { names: [] })
    const body = await readJson<{ code: number }>(response)

    expect(response.status).toBe(400)
    expect(body.code).toBe(ErrorCode.ValidationFailed)
  })
})

/* ==================== 网络 ==================== */

describe('网络 mock 契约', () => {
  it('列表项**没有** containerCount', async () => {
    const response = await post('/networks/list', {})
    const raw = (await response.json()) as {
      data: { items: Array<Record<string, unknown>> }
    }

    // ⚠️ Docker 的列表接口不返回 Containers，后端因此**刻意不给**这个字段。
    // mock 若补上，前端就会基于一个后端永远不会给的字段写逻辑。
    expect(raw.data.items.length).toBeGreaterThan(0)
    for (const item of raw.data.items) {
      expect(item).not.toHaveProperty('containerCount')
    }
  })

  it('内置网络都在且名字唯一', async () => {
    const body = await readJson<{ data: NetworkListResult }>(await post('/networks/list', {}))
    const names = body.data.items.map((item) => item.name)

    expect(names).toContain('bridge')
    expect(names).toContain('host')
    expect(names).toContain('none')
    expect(new Set(names).size).toBe(names.length)
  })

  it('网络 ID 不带 sha256 前缀', async () => {
    const body = await readJson<{ data: NetworkListResult }>(await post('/networks/list', {}))
    const id = body.data.items[0].id

    expect(id).toMatch(/^[0-9a-f]{64}$/)
    expect(id.startsWith('sha256:')).toBe(false)
  })

  it('同名创建网络返回 409', async () => {
    await post('/networks/create', { name: 'dup-net' })
    const response = await post('/networks/create', { name: 'dup-net' })
    const body = await readJson<{ code: number }>(response)

    // ⚠️ 与卷相反：同名建卷成功，同名建网报错
    expect(response.status).toBe(409)
    expect(body.code).toBe(ErrorCode.DockerResourceConflict)
  })

  it.each([
    ['172.18.0.0', '缺前缀长度'],
    ['172.18.0.0/33', 'IPv4 前缀越界'],
    ['fd00::/200', 'IPv6 前缀越界'],
    ['not-an-ip/16', '完全不是网段'],
    ['999.1.1.1/16', '八位组越界']
  ])('网段 %s 返回 1000（%s）', async (subnet) => {
    const response = await post('/networks/create', { name: 'bad-net', subnet })
    const body = await readJson<{ code: number }>(response)

    expect(response.status).toBe(400)
    expect(body.code).toBe(ErrorCode.ValidationFailed)
  })

  it('内置网络删除返回 409', async () => {
    const response = await post('/networks/remove', { networkId: 'bridge' })
    const body = await readJson<{ code: number; message: string }>(response)

    expect(response.status).toBe(409)
    expect(body.code).toBe(ErrorCode.DockerResourceConflict)
    expect(body.message).toContain('bridge')
  })

  it('有容器接入的网络删除返回 409 并点明数量', async () => {
    const response = await post('/networks/remove', { networkId: 'app-internal' })
    const body = await readJson<{ code: number; message: string }>(response)

    expect(response.status).toBe(409)
    expect(body.code).toBe(ErrorCode.DockerResourceConflict)
    expect(body.message).toContain('1')
  })

  it('详情里的容器端点带 IP（列表里没有）', async () => {
    const body = await readJson<{ data: NetworkDetail }>(
      await post('/networks/inspect', { networkId: 'bridge' })
    )

    expect(body.data.containers.length).toBeGreaterThan(0)
    expect(body.data.containers[0].ipv4Address).toMatch(/^172\./)
  })

  it('IPv6 网段自动打开 enableIPv6', async () => {
    const created = await readJson<{ data: { id: string } }>(
      await post('/networks/create', { name: 'v6-net', subnet: 'fd00:dead:beef::/64' })
    )
    const detail = await readJson<{ data: NetworkDetail }>(
      await post('/networks/inspect', { networkId: created.data.id })
    )

    // 不自动打开的话会建出「有 IPv6 地址池却不通」的网，而界面上看不出异常
    expect(detail.data.enableIPv6).toBe(true)
  })

  it('空场景下列表为空数组', async () => {
    setMockScenario('empty')

    const volumes = await readJson<{ data: VolumeListResult }>(await post('/volumes/list', {}))
    const networks = await readJson<{ data: NetworkListResult }>(await post('/networks/list', {}))

    expect(volumes.data.items).toEqual([])
    expect(networks.data.items).toEqual([])
  })

  it('错误场景下返回 9999', async () => {
    setMockScenario('error')

    const volumes = await post('/volumes/list', {})
    const networks = await post('/networks/list', {})
    const volumeBody = await readJson<{ code: number }>(volumes)
    const networkBody = await readJson<{ code: number }>(networks)

    expect(volumes.status).toBe(500)
    expect(volumeBody.code).toBe(ErrorCode.InternalError)
    expect(networks.status).toBe(500)
    expect(networkBody.code).toBe(ErrorCode.InternalError)
  })
})
