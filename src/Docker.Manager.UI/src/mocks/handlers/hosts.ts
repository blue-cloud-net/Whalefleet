import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, hostsFor, type MockScenario } from '../scenarios'
import type { HostOverview } from '@/types/api'

/**
 * 主机相关端点 mock。
 *
 * 覆盖三类端点：
 *  1. `POST /api/v1/hosts/list`            —— **当前用户可见**的主机（非 admin 也要能用）
 *  2. `POST /api/v1/admin/hosts/list`      —— 主机管理页专用（admin）
 *  3. `POST /api/v1/hosts/{hostId}/overview` —— 聚合 `version` / `counts`（较重，单独一个）
 *
 * 为什么列表与聚合要分开：列表会对每台主机做**轻量 ping** 取连通状态（便宜），
 * 而 `version` / `counts` 需要完整调用 Docker API（贵）。若都塞进列表，
 * 主机一多就会明显变慢甚至超时。分开后总览页一个请求就能画出行与状态。
 *
 * 复刻后端 `CurrentHostMiddleware` 的两个关键分支：
 *  - 主机不存在 → `1300` / 404
 *  - `enabled = false` → `1302` / 400
 * UI 目前还没处理这两个错误码，mock 让它们能被提前演练。
 */

/** 从请求路径里取 hostId（路径形如 `/api/v1/hosts/{hostId}/overview`）。 */
function hostIdFrom(request: Request): string {
  const m = new URL(request.url).pathname.match(/\/hosts\/([^/]+)\/overview$/)
  return m ? decodeURIComponent(m[1]) : ''
}

export function hostHandlers(scenario: () => MockScenario) {
  /** 取当前场景下的主机，并按 id 查出单台。 */
  const findHost = (id: string): HostOverview | undefined =>
    hostsFor(scenario()).find((h) => h.id === id)

  /** 主机列表（两个列表端点共用）。 */
  const listResponse = async () => {
    await delay(delayFor(scenario()))

    // error 场景：列表整体失败，用于验证页面的错误态 + 重试
    if (scenario() === 'error') {
      return HttpResponse.json(fail(ErrorCode.InternalError), {
        status: httpStatusOf(ErrorCode.InternalError)
      })
    }

    return HttpResponse.json(ok(hostsFor(scenario())))
  }

  return [
    // ===== 当前用户可见主机（总览页 / 侧栏 HostSelector 都用它）=====
    http.post('*/api/v1/hosts/list', listResponse),

    // ===== 主机管理页（admin 专用）=====
    http.post('*/api/v1/admin/hosts/list', listResponse),

    // ===== 单台主机的聚合信息（version / counts）=====
    http.post('*/api/v1/hosts/:hostId/overview', async ({ request }) => {
      await delay(delayFor(scenario()))

      const hostId = hostIdFrom(request)
      const host = findHost(hostId)

      // 主机不存在 → 1300/404（与 CurrentHostMiddleware 一致）
      if (!host) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      // 主机已停用 → 1302/400（与 IDockerHostAccessor 校验一致）
      if (!host.enabled) {
        return HttpResponse.json(fail(ErrorCode.HostDisabled), {
          status: httpStatusOf(ErrorCode.HostDisabled)
        })
      }

      // 离线主机：连不上就没有 version / counts，与真实行为一致
      if (host.status === 'offline') {
        return HttpResponse.json(ok({ ...host, version: undefined, counts: undefined }))
      }

      return HttpResponse.json(ok(host))
    })
  ]
}