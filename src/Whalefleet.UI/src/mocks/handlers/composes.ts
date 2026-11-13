import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import { MOCK_COMPOSE_PROJECTS_DEFAULT, MOCK_COMPOSE_PROJECT_DETAILS } from '../data/composes'
import type {
  ComposeProjectDetail,
  ComposeProjectListResult,
  ComposeProjectSummary,
  ResourceTaskSnapshot,
  ComposeWebhookInfo
} from '@/types/api'

/**
 * Compose 项目端点 mock（0.10.0）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 * 1. **部署类端点返回 202**：不是 200。前端若把 202 当失败，会把「已受理」报成「部署失败」。
 * 2. **同名部署报 1902、更新不存在的报 1900**：两者方向相反，是同一个「重名」概念的两面。
 * 3. **任务状态随时间推进**：`queued → running → succeeded`。
 *    mock 若永远返回 `queued`，前端的「完成后刷新列表」分支永远测不到。
 * 4. **同一 Compose 项目同时只能有一个任务**：第二个请求报 1905。
 * 5. **Webhook 令牌明文只给一次**：之后再查只有 `tokenHint`。
 * 6. **YAML 非法报 1904 并带出字段路径**：错误消息里要能看到 `services.xxx`。
 */

/** 会话内新建的编排。 */
const createdComposeProjects = new Map<string, ComposeProjectDetail>()

/** 会话内被删除的编排名。 */
const removedComposeProjects = new Set<string>()

/** 会话内的任务。 */
interface MockTask {
  taskId: string
  hostId: string
  composeName: string
  operation: string
  /** 入队时刻（毫秒时间戳） */
  queuedAt: number
  /** 失败原因；非空时任务终态为 failed */
  error: string | null
  errorCode: number | null
}

const tasks = new Map<string, MockTask>()

/** 会话内的 Webhook 令牌（Compose 项目名 → 令牌明文）。 */
const webhookTokens = new Map<string, string>()

/** 任务从入队到跑完的时长（毫秒）。mock 里调得很短，否则走查要干等。 */
const TASK_DURATION_MS = 1200

/** 清空会话内的改动（供测试隔离用）。 */
export function resetSessionComposeProjects(): void {
  createdComposeProjects.clear()
  removedComposeProjects.clear()
  tasks.clear()
  webhookTokens.clear()
}

/**
 * Compose 项目端点 mock。
 *
 * @param scenario 场景读取器。
 * @returns MSW handler 列表。
 */
export function composeHandlers(scenario: () => MockScenario) {
  /** 读取请求体。 */
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /** 与后端一致的分页口径。 */
  function paginate<T>(items: T[], body: Record<string, unknown>) {
    const rawIndex = Number(body.pageIndex ?? 0)
    const rawSize = Number(body.pageSize ?? 20)
    const pageIndex = Math.max(Number.isFinite(rawIndex) ? Math.trunc(rawIndex) : 0, 0)
    const pageSize = Math.min(
      Math.max(Number.isFinite(rawSize) ? Math.trunc(rawSize) : 20, 1),
      100
    )

    return {
      items: items.slice(pageIndex * pageSize, pageIndex * pageSize + pageSize),
      total: items.length,
      pageIndex,
      pageSize
    }
  }

  /** 当前场景下的编排列表（夹具 + 会话内新建 − 会话内删除）。 */
  function composeProjectsFor(current: MockScenario): ComposeProjectSummary[] {
    if (current === 'empty') return []

    return [
      ...MOCK_COMPOSE_PROJECTS_DEFAULT.filter((compose) => !removedComposeProjects.has(compose.name)),
      ...[...createdComposeProjects.values()].map((detail) => detail.summary)
    ]
  }

  /** 按名称取编排详情。 */
  function detailFor(current: MockScenario, name: string): ComposeProjectDetail | undefined {
    if (current === 'empty') return undefined

    const created = createdComposeProjects.get(name)
    if (created) return created

    if (removedComposeProjects.has(name)) return undefined

    return MOCK_COMPOSE_PROJECT_DETAILS.find((detail) => detail.summary.name === name)
  }

  /**
   * 任务状态随时间推进。
   *
   * ⚠️ **不能永远返回 `queued`**：那样前端的「完成后刷新列表」分支永远走不到，
   * 而「部署完列表还是旧的」正是这个功能最容易出的问题。
   */
  function snapshotOf(task: MockTask): ResourceTaskSnapshot {
    const elapsed = Date.now() - task.queuedAt
    const finished = elapsed >= TASK_DURATION_MS
    const running = !finished && elapsed >= TASK_DURATION_MS / 3

    const status = task.error ? 'failed' : finished ? 'succeeded' : running ? 'running' : 'queued'

    return {
      taskId: task.taskId,
      hostName: task.hostId,
      resourceKind: 'compose',
      resourceName: task.composeName,
      operation: task.operation,
      status,
      percent: finished ? 100 : running ? 45 : null,
      step: finished ? '完成' : running ? '服务 app' : '排队中',
      errorCode: task.errorCode,
      errorMessage: task.error,
      warnings: [],
      result: finished
        ? {
            created: task.operation === 'deploy' ? 1 : 0,
            recreated: 0,
            removed: 0,
            skippedOrphans: 0,
            started: 1,
            stopped: task.operation === 'stop' ? 1 : 0,
            networksCreated: task.operation === 'deploy' ? 1 : 0,
            volumesCreated: 0,
            volumesRemoved: 0
          }
        : null,
      createdAt: new Date(task.queuedAt).toISOString(),
      updatedAt: new Date(task.queuedAt + elapsed).toISOString()
    }
  }

  /** 该 Compose 项目是否已有进行中的任务。 */
  function hasRunningTask(composeName: string): boolean {
    for (const task of tasks.values()) {
      if (task.composeName !== composeName) continue

      const status = snapshotOf(task).status
      if (status === 'succeeded' || status === 'failed') continue

      return true
    }

    return false
  }

  /** 极简 YAML 合法性检查：只查「有没有 services」与「有没有不支持的字段」。 */
  function validateCompose(content: string): string | null {
    if (!content.includes('services:')) {
      return 'Compose 文件缺少「services」段'
    }

    const unsupported = ['build:', 'deploy:', 'secrets:', 'configs:']
    for (const keyword of unsupported) {
      if (content.includes(keyword)) {
        return `services.app.${keyword.replace(':', '')} 暂不支持`
      }
    }

    return null
  }

  /** 受理一次编排操作。 */
  function accept(
    taskId: string,
    hostId: string,
    composeName: string,
    operation: string
  ): string {
    tasks.set(taskId, {
      taskId,
      hostId,
      composeName,
      operation,
      queuedAt: Date.now(),
      error: null,
      errorCode: null
    })

    return taskId
  }

  return [
    /* ==================== 列表 ==================== */

    http.post('*/api/v1/hosts/:hostId/composes/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)

      // ⚠️ 搜索发生在**分页之前**（后端先过滤再切片）：
      // 否则 total 是过滤前的条数，页脚显示的数字与翻页结果对不上。
      const search = String(body.search ?? '').trim().toLowerCase()
      const matched = composeProjectsFor(scenario()).filter((compose) => {
        if (search.length === 0) return true

        return compose.name.toLowerCase().includes(search)
      })

      const paged = paginate(matched, body)

      return HttpResponse.json(ok(paged satisfies ComposeProjectListResult))
    }),

    /* ==================== 详情 ==================== */

    http.post('*/api/v1/hosts/:hostId/composes/inspect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const detail = name ? detailFor(scenario(), name) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.ComposeNotFound), {
          status: httpStatusOf(ErrorCode.ComposeNotFound)
        })
      }

      return HttpResponse.json(ok(detail))
    }),

    /* ==================== 部署 / 更新 ==================== */

    http.post('*/api/v1/hosts/:hostId/composes/deploy', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const content = String(body.composeContent ?? '')
      const taskId = String(body.taskId ?? '').trim()

      if (!name) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const invalid = validateCompose(content)
      if (invalid) {
        return HttpResponse.json(fail(ErrorCode.ComposeInvalid, invalid), {
          status: httpStatusOf(ErrorCode.ComposeInvalid)
        })
      }

      if (composeProjectsFor(scenario()).some((compose) => compose.name === name)) {
        return HttpResponse.json(fail(ErrorCode.ComposeNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.ComposeNameAlreadyExists)
        })
      }

      if (hasRunningTask(name)) {
        return HttpResponse.json(fail(ErrorCode.ComposeOperationInProgress), {
          status: httpStatusOf(ErrorCode.ComposeOperationInProgress)
        })
      }

      const hostId = String(params.hostId)

      // 会话内记住：部署之后紧接着的列表请求要能看到它
      const summary: ComposeProjectSummary = {
        name,
        managed: true,
        status: 'running',
        serviceCount: 1,
        containerCount: 1,
        runningCount: 1,
        networkCount: 1,
        volumeCount: 0,
        composeAvailable: true,
        webhookEnabled: false,
        webhookTokenHint: null,
        webhookLastUsedAt: null,
        updatedAt: new Date().toISOString()
      }

      createdComposeProjects.set(name, {
        summary,
        composeContent: content,
        services: [
          {
            name: 'app',
            image: 'alpine:3.20',
            containerId: 'de819f2a4c5b6d3e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f708192a3b4c',
            containerName: `${name}-app-1`,
            state: 'running',
            status: 'Up 2 seconds',
            health: 'none',
            ports: [],
            restartPolicy: 'unless-stopped'
          }
        ],
        warnings: [],
        // 刚部署完：声明了 1 个服务且容器已建好 → 没有缺失
        declaredServiceCount: 1,
        missingServices: []
      })

      accept(taskId, hostId, name, 'deploy')

      // ⚠️ 202 而不是 200：部署是异步的
      return HttpResponse.json(ok({ taskId, status: 'queued', queuePosition: 1 }), {
        status: 202
      })
    }),

    http.post('*/api/v1/hosts/:hostId/composes/update', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const content = String(body.composeContent ?? '')
      const taskId = String(body.taskId ?? '').trim()

      const detail = name ? detailFor(scenario(), name) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.ComposeNotFound), {
          status: httpStatusOf(ErrorCode.ComposeNotFound)
        })
      }

      if (!detail.summary.managed) {
        return HttpResponse.json(fail(ErrorCode.ComposeNotManaged), {
          status: httpStatusOf(ErrorCode.ComposeNotManaged)
        })
      }

      const invalid = validateCompose(content)
      if (invalid) {
        return HttpResponse.json(fail(ErrorCode.ComposeInvalid, invalid), {
          status: httpStatusOf(ErrorCode.ComposeInvalid)
        })
      }

      if (hasRunningTask(name)) {
        return HttpResponse.json(fail(ErrorCode.ComposeOperationInProgress), {
          status: httpStatusOf(ErrorCode.ComposeOperationInProgress)
        })
      }

      detail.composeContent = content
      detail.summary.updatedAt = new Date().toISOString()

      accept(taskId, String(params.hostId), name, 'update')

      return HttpResponse.json(ok({ taskId, status: 'queued', queuePosition: 1 }), {
        status: 202
      })
    }),

    /* ==================== 停止 / 删除 ==================== */

    http.post('*/api/v1/hosts/:hostId/composes/stop', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const taskId = String(body.taskId ?? '').trim()

      const detail = name ? detailFor(scenario(), name) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.ComposeNotFound), {
          status: httpStatusOf(ErrorCode.ComposeNotFound)
        })
      }

      if (hasRunningTask(name)) {
        return HttpResponse.json(fail(ErrorCode.ComposeOperationInProgress), {
          status: httpStatusOf(ErrorCode.ComposeOperationInProgress)
        })
      }

      accept(taskId, String(params.hostId), name, 'stop')

      return HttpResponse.json(ok({ taskId, status: 'queued', queuePosition: 1 }), {
        status: 202
      })
    }),

    http.post('*/api/v1/hosts/:hostId/composes/remove', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const taskId = String(body.taskId ?? '').trim()

      if (name) {
        removedComposeProjects.add(name)
        createdComposeProjects.delete(name)
      }

      accept(taskId, String(params.hostId), name, 'remove')

      return HttpResponse.json(ok({ taskId, status: 'queued', queuePosition: 1 }), {
        status: 202
      })
    }),

    /* ==================== 任务 ==================== */

    http.post('*/api/v1/hosts/:hostId/composes/task', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const taskId = String(body.taskId ?? '').trim()
      const task = tasks.get(taskId)

      // 任务不存在返回 **null** 而不是报错：任务登记表只保留有限时长
      return HttpResponse.json(ok(task ? snapshotOf(task) : null))
    }),

    /* ==================== Webhook ==================== */

    http.post('*/api/v1/hosts/:hostId/composes/webhook/rotate', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const detail = name ? detailFor(scenario(), name) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.ComposeNotFound), {
          status: httpStatusOf(ErrorCode.ComposeNotFound)
        })
      }

      const token = Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2)
      webhookTokens.set(name, token)

      detail.summary.webhookEnabled = true
      detail.summary.webhookTokenHint = token.slice(0, 8)

      // ⚠️ 明文只此一次：之后再查只有 tokenHint
      const info: ComposeWebhookInfo = {
        enabled: true,
        tokenHint: token.slice(0, 8),
        token,
        lastUsedAt: detail.summary.webhookLastUsedAt
      }

      return HttpResponse.json(ok(info))
    }),

    http.post('*/api/v1/hosts/:hostId/composes/webhook/revoke', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const detail = name ? detailFor(scenario(), name) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.ComposeNotFound), {
          status: httpStatusOf(ErrorCode.ComposeNotFound)
        })
      }

      webhookTokens.delete(name)
      detail.summary.webhookEnabled = false
      detail.summary.webhookTokenHint = null

      const info: ComposeWebhookInfo = {
        enabled: false,
        tokenHint: null,
        token: null,
        lastUsedAt: detail.summary.webhookLastUsedAt
      }

      return HttpResponse.json(ok(info))
    }),

    /* ==================== Webhook 触发（匿名） ==================== */

    http.post('*/api/v1/webhooks/composes/:token', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const token = String(params.token ?? '')
      const entry = [...webhookTokens.entries()].find(([, value]) => value === token)

      if (!entry) {
        // ⚠️ 令牌无效一律 404，不区分「不存在 / 已吊销 / 未启用」
        return HttpResponse.json(fail(ErrorCode.ComposeWebhookTokenInvalid), {
          status: httpStatusOf(ErrorCode.ComposeWebhookTokenInvalid)
        })
      }

      const body = await readBody(request)
      const taskId = `wh-${Date.now().toString(36)}`

      // body 里的 pullAlways / pruneOrphans / wait 目前不影响 mock 的行为，
      // 但读出来能保证请求体被完整消费（MSW 对未读的 body 没有要求，
      // 这里读它只是为了让「触发参数」这条路径在 mock 里也走一遍）。
      void body

      accept(taskId, 'local', entry[0], 'update')

      return HttpResponse.json(ok({ taskId, status: 'queued', queuePosition: 1 }), {
        status: 202
      })
    })
  ]
}
