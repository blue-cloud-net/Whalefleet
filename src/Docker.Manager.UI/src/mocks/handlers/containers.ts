import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import {
  MOCK_CONTAINER_DETAILS,
  MOCK_CONTAINER_LOGS,
  MOCK_CONTAINERS_DEFAULT
} from '../data/containers'
import type { ContainerDetail, ContainerSummary } from '@/types/api'

/**
 * 本次会话里「创建出来」的容器。
 *
 * ⚠️ 必须有这个东西，否则 mock 的「创建 → 启动」链路是断的：
 * `create` 只能返回一个编造的 ID，而紧接着的 `start` 去查容器列表时找不到它，
 * 于是回 1401——界面表现成「刚创建就启动失败」，让人以为是前端的 bug。
 * 真实守护进程当然不是这样（创建出来的容器立刻就可操作），mock 必须同样有记忆。
 *
 * 只存在内存里，不持久化：刷新页面重置是合理的（Mock 本来就是一次性夹具）。
 */
const createdInSession = new Map<string, CreatedContainer>()

/** 本次会话新建的容器：详情 + 列表项需要的端口映射（`ContainerDetail` 里没有端口字段）。 */
interface CreatedContainer {
  detail: ContainerDetail
  ports: ContainerSummary['ports']
}

/** 清空会话内创建的容器（供测试隔离用）。 */
export function resetSessionContainers(): void {
  createdInSession.clear()
}

/**
 * 容器端点 mock（对应后端 0.0.9 已实现的八个端点）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 * mock 的价值不在于「返回一份假数据」，而在于**让前端的分支能被走查到**。
 * 因此这里刻意复刻：
 *
 * 1. **`force` 与 1404**：不带 force 删除运行中的容器 → 409/1404。
 *    前端据此提示「先停止」。不模拟它，那段提示永远测不到。
 * 2. **`pageSize` 夹紧**：请求 5000 时回 100。前端若用请求值算「还有没有下一页」，
 *    就在这里露馅（真实后端同样会夹紧）。
 * 3. **`logs` 的 `tail` 越界**：> 10000 → 1000/400（不是静默截断）。
 * 4. **容器不存在**：→ 1401/404。
 * 5. **`create` 的 warnings**：创建成功同时回带提示。
 */
export function containerHandlers(scenario: () => MockScenario) {
  /** 路径形如 `/api/v1/hosts/{hostId}/containers/{action}`，取 hostId 与 action。 */
  function parse(request: Request): { hostId: string; action: string } {
    const match = new URL(request.url).pathname.match(/\/hosts\/([^/]+)\/containers\/([^/]+)$/)

    return {
      hostId: match ? decodeURIComponent(match[1]) : '',
      action: match ? match[2] : ''
    }
  }

  /** 当前场景下的容器列表（含本次会话新建的）。 */
  function containersFor(current: MockScenario): ContainerSummary[] {
    if (current === 'empty') return []

    return [...MOCK_CONTAINERS_DEFAULT, ...[...createdInSession.values()].map((item) => toSummary(item))]
  }

  /** 全部可查详情的容器（夹具 + 会话新建）。 */
  function detailsFor(current: MockScenario): ContainerDetail[] {
    if (current === 'empty') return []

    return [...MOCK_CONTAINER_DETAILS, ...[...createdInSession.values()].map((item) => item.detail)]
  }

  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  return [
    http.post('*/api/v1/hosts/:hostId/containers/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)
      const pageIndex = Math.max(Number(body.pageIndex ?? 0) || 0, 0)
      const pageSize = Math.min(Math.max(Number(body.pageSize ?? 20) || 20, 1), 100)
      const all = body.all !== false

      // `all: false` 等价 `docker ps`：只列运行中的。
      const visible = containersFor(scenario()).filter((item) => all || item.state === 'running')

      // ⚠️ 排序必须在**分页之前**，否则 total 不变而每页内容错位——
      // 这正是后端 Sort() 的位置，前端排序之所以正确全靠这一点。
      // 规则与后端 ContainerService.Sort 逐条对齐（同值用 id 兜底、未知字段回落 created）。
      const sortBy = String(body.sortBy ?? '').trim()
      const descending = String(body.sortDir ?? '').trim().toLowerCase() !== 'asc'

      let ordered = visible
      if (sortBy.length > 0) {
        const pick = (item: ContainerSummary): string => {
          switch (sortBy) {
            case 'name':
              return item.name
            case 'image':
              return item.image
            case 'state':
              return item.state
            default:
              return item.created
          }
        }

        ordered = [...visible].sort((a, b) => {
          const result = pick(a).localeCompare(pick(b))
          if (result !== 0) return descending ? -result : result
          return a.id.localeCompare(b.id)
        })
      }

      return HttpResponse.json(
        ok({
          items: ordered.slice(pageIndex * pageSize, (pageIndex + 1) * pageSize),
          total: ordered.length,
          pageIndex,
          // 回显**夹紧后**的值：前端必须用它而不是请求里的值。
          pageSize
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/containers/inspect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.containerId ?? '')
      const detail = detailsFor(scenario()).find((item) => item.id === id)

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      return HttpResponse.json(ok(detail))
    }),

    http.post('*/api/v1/hosts/:hostId/containers/logs', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const tail = body.tail === undefined ? null : Number(body.tail)

      // 与后端一致：tail 越界是报错而不是静默截断。
      if (tail !== null && (!Number.isInteger(tail) || tail < 0 || tail > 10000)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      return HttpResponse.json(
        ok({
          containerId: String(body.containerId ?? ''),
          content: tail === 0 ? '' : MOCK_CONTAINER_LOGS
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/containers/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const image = String(body.image ?? '')

      if (image.trim().length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 镜像不在本地 → 1401（复刻后端：守护进程返回 404 时翻译成「资源不存在」）。
      if (image.startsWith('missing')) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // 记住这个容器：否则接下来的 `start` / `inspect` 会因找不到它而回 1401，
      // 界面表现成「刚创建就启动失败」，而真实守护进程并非如此。
      const containerId = `e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6${createdInSession.size}`

      createdInSession.set(containerId, createFixture(containerId, body))
      return HttpResponse.json(
        ok({
          containerId,
          // 真实守护进程会在这里给提示（例如「端口未映射」）。
          // 前端必须把它当「提示」而不是「失败」渲染。
          warnings: body.ports ? [] : ['该容器没有端口映射，无法从宿主机访问']
        })
      )
    }),

    ...(['start', 'stop', 'restart'] as const).map((action) =>
      http.post(`*/api/v1/hosts/:hostId/containers/${action}`, async ({ request }) => {
        await delay(delayFor(scenario()))

        const { hostId } = parse(request)
        const body = await readBody(request)
        const containerId = String(body.containerId ?? '')

        if (containerId.trim().length === 0) {
          return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
            status: httpStatusOf(ErrorCode.ValidationFailed)
          })
        }

        // 未知容器 → 1401；这三个端点都是**幂等**的，因此不需要模拟 304。
        if (!containersFor(scenario()).some((item) => item.id === containerId)) {
          return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
            status: httpStatusOf(ErrorCode.DockerResourceNotFound)
          })
        }

        // stop 的 waitSeconds 越界 → 1000（与后端一致：0–300）。
        if (action === 'stop' && body.waitSeconds != null) {
          const wait = Number(body.waitSeconds)
          if (!Number.isInteger(wait) || wait < 0 || wait > 300) {
            return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
              status: httpStatusOf(ErrorCode.ValidationFailed)
            })
          }
        }

        void hostId

        // 会话新建的容器要真的跟着变状态：否则界面会显示「启动成功」但行里仍是「已创建」，
        // 走查时看起来像前端没刷新，而真相是 mock 没记事。
        applyStateChange(containerId, action)

        return HttpResponse.json(ok(null))
      })
    ),

    http.post('*/api/v1/hosts/:hostId/containers/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const containerId = String(body.containerId ?? '')
      const force = body.force === true
      const container = containersFor(scenario()).find((item) => item.id === containerId)

      if (!container) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // ⚠️ 这一条最容易漏：不带 force 删运行中的容器，守护进程返回 409。
      // 前端要靠它提示「先停止容器」，因此 mock 必须能产生这个分支。
      if (container.state === 'running' && !force) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceConflict), {
          status: httpStatusOf(ErrorCode.DockerResourceConflict)
        })
      }

      // 删除成功则从会话记忆里移除：否则删完再点「刷新」它又回来了。
      createdInSession.delete(containerId)

      return HttpResponse.json(ok(null))
    })
  ]
}

/**
 * 把状态变更应用到会话新建的容器上。
 *
 * 夹具（`MOCK_CONTAINER_DETAILS`）是模块级常量，刻意不改它们：
 * 改了就变成「跑一次走查后夹具变了」，下一轮看到的状态与初始态不一致
 * （而且并行跑测试时互相污染）。只有本轮会话新建的容器是可变的。
 */
function applyStateChange(containerId: string, action: 'start' | 'stop' | 'restart'): void {
  const created = createdInSession.get(containerId)

  if (!created) return

  const { state } = created.detail

  if (action === 'stop') {
    state.status = 'exited'
    state.running = false
    state.finishedAt = new Date().toISOString()
    // 优雅停止成功，Mock 固定给 0（真实场景可能是 137，那取决于进程是否响应 SIGTERM）。
    state.exitCode = 0
    return
  }

  state.status = 'running'
  state.running = true
  state.startedAt = state.startedAt ?? new Date().toISOString()
  state.finishedAt = null
  state.exitCode = null
}

/**
 * 由创建请求造出一个「已创建但未启动」的容器详情。
 *
 * 状态必须是 `created` 而**不是** `running`——「创建不自动启动」是后端的明确语义
 * （见 api-spec 的变更端点说明）。mock 若在这里返回 running，界面会立刻显示成运行中，
 * 用户点「启动」时反而看到「已启动」的空操作，把真实语义掩盖掉。
 */
function createFixture(id: string, body: Record<string, unknown>): CreatedContainer {
  const cmd = Array.isArray(body.cmd) ? (body.cmd as string[]) : []

  // 端口映射要一并推出来：新建的容器如果永远显示「无端口」，
  // 用户会以为端口映射没生效（而它其实生效了）。
  const ports: ContainerSummary['ports'] = Array.isArray(body.ports)
    ? (
        body.ports as Array<{ containerPort: number; hostPort?: number; type?: string; hostIp?: string }>
      ).map((port) => ({
        privatePort: port.containerPort,
        publicPort: port.hostPort ?? null,
        type: port.type ?? 'tcp',
        ip: port.hostIp ?? null
      }))
    : []

  const detail: ContainerDetail = {
    id,
    name: String(body.name ?? 'generated-name'),
    created: new Date().toISOString(),
    image: String(body.image ?? ''),
    state: {
      status: 'created',
      running: false,
      paused: false,
      restarting: false,
      startedAt: null,
      finishedAt: null,
      exitCode: null,
      oomKilled: false,
      healthStatus: null
    },
    config: {
      image: String(body.image ?? ''),
      cmd,
      entrypoint: Array.isArray(body.entrypoint) ? (body.entrypoint as string[]) : [],
      env: Array.isArray(body.env) ? (body.env as string[]) : [],
      labels: (body.labels as Record<string, string>) ?? {},
      workingDir: String(body.workingDir ?? ''),
      hostname: String(body.hostname ?? id.slice(0, 12)),
      user: '',
      tty: body.tty === true
    },
    hostConfig: {
      restartPolicy: String(body.restartPolicy ?? 'no'),
      autoRemove: body.autoRemove === true,
      networkMode: String(body.network ?? 'bridge'),
      privileged: false,
      memoryLimitBytes: null,
      nanoCpus: null
    },
    mounts: Array.isArray(body.volumes)
      ? (body.volumes as Array<{ source: string; destination: string; readOnly?: boolean }>).map(
          (volume) => ({
            type: 'bind',
            source: volume.source,
            destination: volume.destination,
            readWrite: volume.readOnly !== true
          })
        )
      : [],
    networks: []
  }

  return { detail, ports }
}

/**
 * 由详情派生列表项，端口由创建请求里的映射推出。
 *
 * 端口必须一起推出来：新建的容器如果永远显示「无端口」，用户会以为端口映射没生效。
 */
function toSummary(item: CreatedContainer): ContainerSummary {
  const { detail, ports } = item

  return {
    id: detail.id,
    name: detail.name,
    image: detail.image,
    imageId: `sha256:${detail.id.slice(0, 32)}`,
    command: detail.config.cmd.join(' '),
    created: detail.created,
    state: detail.state.status,
    status: detail.state.running ? 'Up 0 seconds' : 'Created',
    ports
  }
}
