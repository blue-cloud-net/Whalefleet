import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import type { ImageUpdatePolicy } from '@/types/api'

/**
 * 镜像自动更新 mock（0.15.0）。
 *
 * ## 复刻的关键语义：首次检查只建立基线
 * 第一次 `check` **不会**报「有更新」，它只是把远端摘要记为基线。
 * mock 里用一个「摘要生成器」模拟仓库：每次探测给一个新摘要，
 * 因此第二次 `check` 才会出现 `update-available`。
 * 如果 mock 一上来就返回「有更新」，界面上那条「尚未检查」的分支就永远测不到。
 *
 * ## 摘要每次探测都会变
 * 真实仓库里摘要只有在镜像被重新推送后才变，但那需要有人推镜像——
 * mock 选择「每次都变」，是为了让「检查两次 → 第二次报有更新」这个流程可走通。
 */
interface MockPolicy {
  id: string
  imageReference: string
  mode: 'notify' | 'auto'
  enabled: boolean
  state: ImageUpdatePolicy['state']
  baselineDigest: string | null
  remoteDigest: string | null
  message: string | null
  lastCheckedAt: string | null
  lastAppliedAt: string | null
}

const DEFAULT_POLICIES: MockPolicy[] = [
  {
    id: 'policy-1',
    imageReference: 'nginx:1.27',
    mode: 'notify',
    enabled: true,
    state: 'unknown',
    baselineDigest: null,
    remoteDigest: null,
    message: null,
    lastCheckedAt: null,
    lastAppliedAt: null
  }
]

let policies: MockPolicy[] = structuredClone(DEFAULT_POLICIES)

/** 每次探测生成的新摘要序号（保证「摘要会变」）。 */
let probeSequence = 0

/** 清空会话内的策略改动（供测试隔离用）。 */
export function resetSessionImageUpdates(): void {
  policies = structuredClone(DEFAULT_POLICIES)
  probeSequence = 0
}

/**
 * 造一个形如真实摘要的串。
 *
 * @returns `sha256:` + 64 位十六进制。
 */
function nextDigest(): string {
  probeSequence += 1

  return `sha256:${probeSequence.toString(16).padStart(64, '0')}`
}

export function imageUpdateHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /**
   * 跑一次检查，返回新的策略快照。
   *
   * @param policy 目标策略（会被就地更新）。
   * @returns 重建的容器数（`auto` 模式下第二次检查起为 1）。
   */
  function probe(policy: MockPolicy): number {
    const now = new Date().toISOString()
    const digest = nextDigest()

    policy.lastCheckedAt = now
    policy.remoteDigest = digest
    policy.message = null

    if (policy.baselineDigest === null) {
      // 首次检查：只建立基线，不宣称有更新。
      policy.baselineDigest = digest
      policy.state = 'up-to-date'

      return 0
    }

    if (policy.baselineDigest === digest) {
      policy.state = 'up-to-date'

      return 0
    }

    if (policy.mode === 'auto') {
      policy.baselineDigest = digest
      policy.state = 'applied'
      policy.lastAppliedAt = now

      return 1
    }

    policy.state = 'update-available'

    return 0
  }

  return [
    http.post('*/api/v1/hosts/:hostId/images/update-policies/list', async () => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      return HttpResponse.json(ok(policies))
    }),

    http.post('*/api/v1/hosts/:hostId/images/update-policies/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const raw = String(body.reference ?? '').trim()

      if (raw.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 摘要引用永不变化，跟踪它等于永远「已是最新」——后端直接拒绝。
      if (raw.includes('@')) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const reference = /:[^/]*$/.test(raw) ? raw : `${raw}:latest`

      if (policies.some((item) => item.imageReference === reference)) {
        return HttpResponse.json(fail(ErrorCode.ImageUpdatePolicyAlreadyExists), {
          status: httpStatusOf(ErrorCode.ImageUpdatePolicyAlreadyExists)
        })
      }

      const created: MockPolicy = {
        id: `policy-${policies.length + 1}-${Date.now()}`,
        imageReference: reference,
        mode: body.mode === 'auto' ? 'auto' : 'notify',
        enabled: body.enabled == null ? true : Boolean(body.enabled),
        state: 'unknown',
        baselineDigest: null,
        remoteDigest: null,
        message: null,
        lastCheckedAt: null,
        lastAppliedAt: null
      }

      policies = [...policies, created]

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/hosts/:hostId/images/update-policies/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const target = policies.find((item) => item.id === String(body.id ?? ''))

      if (!target) {
        return HttpResponse.json(fail(ErrorCode.ImageUpdatePolicyNotFound), {
          status: httpStatusOf(ErrorCode.ImageUpdatePolicyNotFound)
        })
      }

      const mode = body.mode == null ? null : String(body.mode)

      if (mode !== null && mode !== 'notify' && mode !== 'auto') {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      if (mode !== null) {
        target.mode = mode
      }

      if (body.enabled != null) {
        target.enabled = Boolean(body.enabled)
      }

      return HttpResponse.json(ok(target))
    }),

    http.post('*/api/v1/hosts/:hostId/images/update-policies/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.id ?? '')

      if (!policies.some((item) => item.id === id)) {
        return HttpResponse.json(fail(ErrorCode.ImageUpdatePolicyNotFound), {
          status: httpStatusOf(ErrorCode.ImageUpdatePolicyNotFound)
        })
      }

      policies = policies.filter((item) => item.id !== id)

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/hosts/:hostId/images/update-policies/check', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const target = policies.find((item) => item.id === String(body.id ?? ''))

      if (!target) {
        return HttpResponse.json(fail(ErrorCode.ImageUpdatePolicyNotFound), {
          status: httpStatusOf(ErrorCode.ImageUpdatePolicyNotFound)
        })
      }

      // ⚠️ 仓库不可达 / 需授权 / 限流都是 **200 + state=failed**，
      // 不是 4xx/5xx——那是「检查结论」，不是「请求有错」。
      if (scenario() === 'error') {
        target.state = 'failed'
        target.message = '无法连接仓库'
        target.lastCheckedAt = new Date().toISOString()

        return HttpResponse.json(ok({ policy: target, recreatedContainers: 0 }))
      }

      const recreated = probe(target)

      return HttpResponse.json(ok({ policy: target, recreatedContainers: recreated }))
    })
  ]
}
