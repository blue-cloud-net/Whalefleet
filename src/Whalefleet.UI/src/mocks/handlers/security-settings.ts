import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import type { SecurityPolicyOptions } from '@/types/api'

/**
 * 实例级安全策略 mock（0.13.0）。
 *
 * 只做两件事：读回当前值、保存合法值。
 * 语义上要紧的是**校验边界**：最小长度 1–1024、有效期不能为负；
 * 越界返回 `1000`（与后端一致），而不是静默夹紧——
 * 夹紧会让管理员以为「我填的值生效了」。
 */

const DEFAULT_POLICY: SecurityPolicyOptions = {
  minLength: 10,
  requireLetter: true,
  requireDigitOrSymbol: true,
  passwordExpiryDays: 0,
  mustChangeOnFirstLogin: true
}

let policy: SecurityPolicyOptions = { ...DEFAULT_POLICY }

/** 清空会话内的策略改动（供测试隔离用）。 */
export function resetSessionSecurityPolicy(): void {
  policy = { ...DEFAULT_POLICY }
}

export function securitySettingsHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  return [
    http.post('*/api/v1/admin/settings/security/get', async () => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      return HttpResponse.json(ok(policy))
    }),

    http.post('*/api/v1/admin/settings/security/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const minLength = Number(body.minLength ?? 0)
      const expiryDays = Number(body.passwordExpiryDays ?? 0)

      if (!Number.isInteger(minLength) || minLength < 1 || minLength > 1024) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '最小长度需在 1–1024 之间'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      if (!Number.isInteger(expiryDays) || expiryDays < 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '有效期不能为负'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      policy = {
        minLength,
        requireLetter: Boolean(body.requireLetter),
        requireDigitOrSymbol: Boolean(body.requireDigitOrSymbol),
        passwordExpiryDays: expiryDays,
        mustChangeOnFirstLogin: Boolean(body.mustChangeOnFirstLogin)
      }

      return HttpResponse.json(ok(policy))
    })
  ]
}
