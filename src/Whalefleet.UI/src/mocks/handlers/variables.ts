import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'

/**
 * 环境变量 mock（0.15.0）。
 *
 * ## 复刻的三条易漏语义
 * 1. **值永不回显**：响应里只有 `hasValue` 与固定长度的遮罩串。
 *    返回真值会让前端代码「顺手」把它显示出来，而在真实后端上那个字段根本不存在。
 * 2. **作用域与唯一性**：同一作用域内重名 → `1914`；跨作用域同名是允许的
 *    （项目级会覆盖全局）。
 * 3. **密钥不可被 Webhook 覆盖**：提交时带上 `isSecret && webhookOverridable`
 *    会被**强制置否**（不是报错）——这是后端的不变量，不是校验。
 *
 * ## mock 里没有 Compose 文件
 * `undefined` 清单用一个「引用了但没定义」的固定示例（`IMAGE_TAG`）模拟：
 * 真实实现要看 YAML 里引用了哪些 `${VAR}`，而 mock 不解析 YAML。
 * 有它才能测到界面上那条告警——否则那段分支永远跑不到。
 */
interface MockVariable {
  name: string
  scope: 'global' | 'project'
  hostName: string | null
  composeName: string | null
  isSecret: boolean
  webhookOverridable: boolean
  description: string | null
  hasValue: boolean
  valueMasked: string | null
}

/** 遮罩串长度固定：长度泄露不了真实值的长度。 */
const MASK = '••••••'

/** 变量名规则（与后端 `EnvVariableName` 同一口径）。 */
const NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/

const DEFAULT_GLOBAL: MockVariable[] = [
  {
    name: 'GLOBAL_TAG',
    scope: 'global',
    hostName: null,
    composeName: null,
    isSecret: false,
    webhookOverridable: true,
    description: '所有项目的默认镜像标签',
    hasValue: true,
    valueMasked: MASK
  },
  {
    name: 'REGISTRY_PASSWORD',
    scope: 'global',
    hostName: null,
    composeName: null,
    isSecret: true,
    webhookOverridable: false,
    description: '私有仓库口令',
    hasValue: true,
    valueMasked: MASK
  }
]

const DEFAULT_PROJECT: MockVariable[] = [
  {
    name: 'APP_TAG',
    scope: 'project',
    hostName: 'local',
    composeName: 'demo-stack',
    isSecret: false,
    webhookOverridable: true,
    description: null,
    hasValue: true,
    valueMasked: MASK
  }
]

let globals: MockVariable[] = structuredClone(DEFAULT_GLOBAL)
let projects: MockVariable[] = structuredClone(DEFAULT_PROJECT)

/** 清空会话内的变量改动（供测试隔离用）。 */
export function resetSessionVariables(): void {
  globals = structuredClone(DEFAULT_GLOBAL)
  projects = structuredClone(DEFAULT_PROJECT)
}

/**
 * 由请求体拼一条变量投影。
 *
 * @param body 请求体。
 * @param scope 作用域。
 * @param composeName 项目名（全局传 null）。
 * @returns 投影；名称为空时返回 null。
 */
function build(
  body: Record<string, unknown>,
  scope: 'global' | 'project',
  composeName: string | null
): MockVariable | null {
  const name = String(body.name ?? '').trim()

  if (name.length === 0) {
    return null
  }

  const isSecret = Boolean(body.isSecret)
  const value = body.value

  return {
    name,
    scope,
    hostName: scope === 'global' ? null : 'local',
    composeName,
    isSecret,
    // 密钥恒不可覆盖：后端强制，mock 也强制（否则前端会以为「勾上了就能生效」）。
    webhookOverridable: isSecret ? false : Boolean(body.webhookOverridable),
    description: body.description == null ? null : String(body.description),
    hasValue: value != null && String(value).length > 0,
    valueMasked: value != null && String(value).length > 0 ? MASK : null
  }
}

export function variableHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /**
   * 把「修改」应用到一条既有变量上。
   *
   * ⚠️ `value` 省略（`null` / 未传）表示**不改值**：这与「清空值」是两件事。
   * 把它们混为一谈的症状是「编辑一次备注，口令就没了」。
   */
  function applyUpdate(target: MockVariable, body: Record<string, unknown>): void {
    target.isSecret = body.isSecret == null ? target.isSecret : Boolean(body.isSecret)
    target.webhookOverridable =
      target.isSecret || body.webhookOverridable == null
        ? target.isSecret
          ? false
          : target.webhookOverridable
        : Boolean(body.webhookOverridable)
    target.description = body.description == null ? null : String(body.description)

    if (body.clearValue === true) {
      target.hasValue = false
      target.valueMasked = null
    } else if (body.value != null && String(body.value).length > 0) {
      target.hasValue = true
      target.valueMasked = MASK
    }
  }

  return [
    // ----- 全局变量 -----
    http.post('*/api/v1/admin/variables/list', async () => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      return HttpResponse.json(ok(globals))
    }),

    http.post('*/api/v1/admin/variables/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()

      if (!NAME_PATTERN.test(name) || name.length > 64) {
        return HttpResponse.json(fail(ErrorCode.InvalidVariableName), {
          status: httpStatusOf(ErrorCode.InvalidVariableName)
        })
      }

      if (globals.some((item) => item.name === name)) {
        return HttpResponse.json(fail(ErrorCode.VariableNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.VariableNameAlreadyExists)
        })
      }

      const created = build(body, 'global', null)

      if (created === null) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      globals = [...globals, created]

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/variables/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '')
      const target = globals.find((item) => item.name === name)

      if (!target) {
        return HttpResponse.json(fail(ErrorCode.VariableNotFound), {
          status: httpStatusOf(ErrorCode.VariableNotFound)
        })
      }

      applyUpdate(target, body)

      return HttpResponse.json(ok(target))
    }),

    http.post('*/api/v1/admin/variables/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '')

      if (!globals.some((item) => item.name === name)) {
        return HttpResponse.json(fail(ErrorCode.VariableNotFound), {
          status: httpStatusOf(ErrorCode.VariableNotFound)
        })
      }

      globals = globals.filter((item) => item.name !== name)

      return HttpResponse.json(ok(null))
    }),

    // ----- 项目级变量 -----
    http.post('*/api/v1/hosts/:hostId/composes/variables/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const composeName = String(body.composeName ?? '')

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      return HttpResponse.json(
        ok({
          project: projects.filter((item) => item.composeName === composeName),
          inherited: globals,
          // 固定一个「引用了但没定义」的变量：这样界面上那条告警始终能被走查到。
          undefined: ['IMAGE_TAG']
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/composes/variables/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const composeName = String(body.composeName ?? '')
      const name = String(body.name ?? '').trim()

      if (!NAME_PATTERN.test(name) || name.length > 64) {
        return HttpResponse.json(fail(ErrorCode.InvalidVariableName), {
          status: httpStatusOf(ErrorCode.InvalidVariableName)
        })
      }

      if (projects.some((item) => item.composeName === composeName && item.name === name)) {
        return HttpResponse.json(fail(ErrorCode.VariableNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.VariableNameAlreadyExists)
        })
      }

      const created = build(body, 'project', composeName)

      if (created === null) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      projects = [...projects, created]

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/hosts/:hostId/composes/variables/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const composeName = String(body.composeName ?? '')
      const name = String(body.name ?? '')
      const target = projects.find(
        (item) => item.composeName === composeName && item.name === name
      )

      if (!target) {
        return HttpResponse.json(fail(ErrorCode.VariableNotFound), {
          status: httpStatusOf(ErrorCode.VariableNotFound)
        })
      }

      applyUpdate(target, body)

      return HttpResponse.json(ok(target))
    }),

    http.post('*/api/v1/hosts/:hostId/composes/variables/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const composeName = String(body.composeName ?? '')
      const name = String(body.name ?? '')

      if (!projects.some((item) => item.composeName === composeName && item.name === name)) {
        return HttpResponse.json(fail(ErrorCode.VariableNotFound), {
          status: httpStatusOf(ErrorCode.VariableNotFound)
        })
      }

      projects = projects.filter(
        (item) => !(item.composeName === composeName && item.name === name)
      )

      return HttpResponse.json(ok(null))
    })
  ]
}
