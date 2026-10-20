import { http, HttpResponse } from 'msw'
import { ErrorCode, fail, httpStatusOf } from './envelope'
import { authHandlers } from './handlers/auth'
import { containerHandlers } from './handlers/containers'
import { hostHandlers } from './handlers/hosts'
import { imageHandlers } from './handlers/images'
import { repositoryHandlers } from './handlers/repositories'
import { roleHandlers } from './handlers/permissions'
import { resourceHandlers } from './handlers/resources'
import { userHandlers } from './handlers/users'
import { resolveScenario, type MockScenario } from './scenarios'

/**
 * 组装全部 mock handler。
 *
 * 场景是**惰性读取**的（传函数而非值）：worker 与 vitest 共用本函数，
 * 而两边切换场景的时机不同——浏览器靠改 URL query，测试靠调用 `setScenario`，
 * 只有惰性求值才能让 handler 在每次请求时拿到当前场景。
 */

/** 测试可写入的当前场景；浏览器侧由 query 决定。 */
let currentScenario: MockScenario = 'default'

export function setMockScenario(scenario: MockScenario): void {
  currentScenario = scenario
}

export function getMockScenario(): MockScenario {
  return currentScenario
}

export const handlers = [
  /**
   * 探测端点：用于确认 SW **真的在拦截**（见 `browser.ts` 的 waitUntilIntercepting）。
   *
   * 带一个 `x-mock: 1` 响应头作为「确实由 mock 处理」的凭据——
   * 只看status 200 不够，它可能来自 Vite 或真实后端。
   */
  http.get('/__mock__/ping', () => {
    return new HttpResponse(null, {
      status: 200,
      headers: { 'x-mock': '1' }
    })
  }),
  ...authHandlers(() => currentScenario),
  ...hostHandlers(() => currentScenario),
  ...containerHandlers(() => currentScenario),
  ...imageHandlers(() => currentScenario),
  ...repositoryHandlers(() => currentScenario),
  ...resourceHandlers(() => currentScenario),
  ...userHandlers(() => currentScenario),
  ...roleHandlers(() => currentScenario)
]

/**
 * 未被 handler 覆盖的请求。
 *
 * 一律 `bypass`（放行到真实后端）而不是 `error`：
 * mock 只覆盖认证 / 主机 / 容器 / 镜像 / 数据卷 / 网络 / 用户 / 角色与授权这些域，
 * 允许「认证走真后端 + 其余走 mock」的混合调试，
 * 也避免新增后端端点时前端被 mock 挡住而误判。
 */
export const onUnhandledRequest = 'bypass' as const

export { http, HttpResponse, ErrorCode, fail, httpStatusOf, resolveScenario }