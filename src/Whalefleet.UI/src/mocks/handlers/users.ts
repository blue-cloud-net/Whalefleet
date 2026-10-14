import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import { MOCK_CURRENT_PASSWORD, MOCK_CURRENT_USERNAME, MOCK_USERS_DEFAULT } from '../data/users'
import type { AdminUserSummary } from '@/types/api'

/**
 * 本次会话里新建 / 删除的用户。
 *
 * 理由与容器、镜像一致：`create` 只返回结果的话，紧接着的列表看不到新人，
 * 界面表现成「创建成功但列表里没有」；删除同理，不清掉就会「删完刷新又回来了」。
 */
const createdInSession: AdminUserSummary[] = []
const removedInSession = new Set<string>()

/** 清空会话内的用户改动（供测试隔离用）。 */
export function resetSessionUsers(): void {
  createdInSession.length = 0
  removedInSession.clear()
}

/**
 * 用户管理端点 mock（对应后端 0.0.8 已实现的四个端点）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 * 1. **`pageSize` 夹紧**（请求 5000 → 100）。
 * 2. **用户名重复 → 1200**；**密码强度不足 → 1202**（两者都是 400，但错误码不同，
 *    界面才能把提示精确落在对应字段上）。
 * 3. **不能删除当前登录账号 → 1000**：这条不复刻，「删自己」的护栏就永远测不到。
 * 4. **不能删除最后一个管理员 → 1000**。
 * 5. **自助改密必须验原密码**：省略 `userId` 且 `oldPassword` 不对 → 1000。
 *    刻意**不是 401**——前端拦截器把 401 当会话过期会自动刷新重放，
 *    用在这里会把「输错原密码」表现成一连串莫名其妙的重定向。
 * 6. **管理员重置他人不需要原密码**（提交 `userId` 即可）。
 *    至于「非 admin 改他人 → 1500」：mock 没有调用者角色的概念（它只认当前用户名），
 *    而那一条靠后端 `[Authorize(Roles)]` 与 `UserService` 保证，这里不假装模拟。
 */
export function userHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  function usersFor(current: MockScenario): AdminUserSummary[] {
    if (current === 'empty') return []

    return [...MOCK_USERS_DEFAULT, ...createdInSession].filter(
      (user) => !removedInSession.has(user.id)
    )
  }

  /** 密码强度：与后端 `PasswordPolicy` 一致（≥10 字符，且含字母与数字/符号）。 */
  function isStrongPassword(password: string): boolean {
    const hasLetter = /[A-Za-z]/.test(password)
    const hasDigitOrSymbol = /[0-9]|[^A-Za-z0-9]/.test(password)

    return password.length >= 10 && hasLetter && hasDigitOrSymbol
  }

  return [
    http.post('*/api/v1/admin/users/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)
      const pageIndex = Math.max(Number(body.pageIndex ?? 0) || 0, 0)
      const pageSize = Math.min(Math.max(Number(body.pageSize ?? 20) || 20, 1), 100)
      const all = usersFor(scenario())

      return HttpResponse.json(
        ok({
          items: all.slice(pageIndex * pageSize, (pageIndex + 1) * pageSize),
          total: all.length,
          pageIndex,
          // 回显**夹紧后**的值
          pageSize
        })
      )
    }),

    http.post('*/api/v1/admin/users/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const username = String(body.username ?? '').trim()
      const password = String(body.password ?? '')
      const role = String(body.role ?? '').trim()

      if (username.length < 3 || username.length > 32 || !/^[A-Za-z0-9._-]+$/.test(username)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '用户名须为 3–32 位，仅允许字母、数字与 _ . -'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      // ⚠️ 必须与服务端一致地判重复，否则界面会在「创建成功」与「列表里已有同名」之间矛盾
      if (usersFor(scenario()).some((user) => user.username.toLowerCase() === username.toLowerCase())) {
        return HttpResponse.json(fail(ErrorCode.UsernameAlreadyExists), {
          status: httpStatusOf(ErrorCode.UsernameAlreadyExists)
        })
      }

      if (!isStrongPassword(password)) {
        return HttpResponse.json(fail(ErrorCode.WeakPassword), {
          status: httpStatusOf(ErrorCode.WeakPassword)
        })
      }

      // role 省略时取默认角色（后端当前是 auditor）
      const created: AdminUserSummary = {
        id: `00000000-0000-0000-0000-000000000${(createdInSession.length + 90).toString(16).padStart(3, '0')}`,
        username,
        role: role.length > 0 ? role : 'auditor',
        creationTime: new Date().toISOString()
      }

      createdInSession.push(created)
      removedInSession.delete(created.id)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/users/delete', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const userId = String(body.userId ?? '')
      const target = usersFor(scenario()).find((user) => user.id === userId)

      if (target === undefined) {
        return HttpResponse.json(fail(ErrorCode.UserNotFound), {
          status: httpStatusOf(ErrorCode.UserNotFound)
        })
      }

      // 护栏一：不能删除当前登录账号（mock 以固定用户名代表当前用户）
      if (target.username === MOCK_CURRENT_USERNAME) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '不能删除当前登录的账号'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 护栏二：不能删除最后一个管理员
      const admins = usersFor(scenario()).filter((user) => user.role === 'admin')
      if (target.role === 'admin' && admins.length <= 1) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '这是最后一个管理员账号，删除后将无人能管理系统'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const createdIndex = createdInSession.findIndex((user) => user.id === userId)
      if (createdIndex >= 0) createdInSession.splice(createdIndex, 1)
      removedInSession.add(userId)

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/admin/users/update-password', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const userId = String(body.userId ?? '').trim()
      const oldPassword = String(body.oldPassword ?? '')
      const newPassword = String(body.newPassword ?? '')
      const users = usersFor(scenario())

      // 省略 userId 或指向自己 → 自助改密（必须验原密码）
      const target =
        userId.length === 0
          ? users.find((user) => user.username === MOCK_CURRENT_USERNAME)
          : users.find((user) => user.id === userId)

      if (target === undefined) {
        return HttpResponse.json(fail(ErrorCode.UserNotFound), {
          status: httpStatusOf(ErrorCode.UserNotFound)
        })
      }

      const isSelf = target.username === MOCK_CURRENT_USERNAME

      if (isSelf && oldPassword !== MOCK_CURRENT_PASSWORD) {
        // 刻意用 1000 而不是 401：401 会被前端拦截器当成会话过期去刷新重放
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '原密码不正确'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      if (!isStrongPassword(newPassword)) {
        return HttpResponse.json(fail(ErrorCode.WeakPassword), {
          status: httpStatusOf(ErrorCode.WeakPassword)
        })
      }

      return HttpResponse.json(ok(null))
    })
  ]
}
