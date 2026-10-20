import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import { MOCK_REPOSITORIES_DEFAULT } from '../data/repositories'
import type { RepositorySummary } from '@/types/api'

/**
 * 本次会话里新增 / 删除的配置。
 *
 * 与镜像夹具同理由：新增后列表必须立刻能看到，否则界面表现成
 * 「保存成功但列表里没有」。而夹具本身**不原地改**——
 * 原地改会让「跑一次走查后夹具变了」，下一轮看到的数据与初始态不一致。
 */
const addedInSession: RepositorySummary[] = []
const removedInSession = new Set<string>()

/** 清空会话内的 Registry 改动（供测试隔离用）。 */
export function resetSessionRepositories(): void {
  addedInSession.length = 0
  removedInSession.clear()
}

/**
 * 当前场景下的配置列表（夹具叠加会话新增 / 删除）。
 *
 * 导出而不是留在 `repositoryHandlers` 闭包里：镜像的 push 端点要判「这个地址有没有
 * 登记凭据」（决定 `authenticated`），两份各写一份就会出现「配置页显示有凭据、
 * 推送时却说匿名」这种互相矛盾的现象。
 */
export function repositoriesFor(current: MockScenario): RepositorySummary[] {
  if (current === 'empty') return []

  return [
    ...MOCK_REPOSITORIES_DEFAULT.filter((item) => !removedInSession.has(item.id)),
    ...addedInSession
  ]
}

/**
 * 仓库端点 mock（对应后端 0.4.0 的 `admin/repositories` 四个端点）。
 *
 * ## 复刻了哪些「容易被忽略的语义」
 * 1. **响应里没有口令，也没有密文**——mock 若好心带一个 `password` 字段，
 *    「界面不回显口令」这条就永远测不到。
 * 2. **别名与地址各占一个唯一索引**：别名冲突 `1801`、地址冲突 `1802`。
 *    地址冲突尤其重要——同址两条配置会让「推送时用哪条凭据」变成随机行为。
 * 3. **`test` 的两种失败是不同的错误码**：地址不可达 `1805`（502）、
 *    凭据被拒 `1804`（400）。界面对这两者的处置完全不同（查网络 / 重填口令），
 *    归成一个码就只能显示「测试失败」。
 * 4. **`test` 可以测「尚未保存」的配置**（不带 `id`）：这是「先试再存」动线的
 *    基础，mock 不实现这条，界面上的「测试连接」按钮就只在保存后才能用。
 *
 * ## 触发失败的方式（与镜像 handler 的 `missing*` / `denied*` 同一套约定）
 * - 地址以 `unreachable` 开头 → `1805`
 * - 地址以 `denied` 开头 → `1804`
 * - 地址以 `open` 开头 → 连得上但**未认证**（返回 `reachable: true, authenticated: false`）
 */
export function repositoryHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /**
   * 探测一根地址，返回「后端会怎么答」。
   *
   * 用判别式而不是 `reachable` 布尔：**「不可达」与「凭据被拒」是两个不同的错误码**
   * （`1805` / `1804`）。先算成布尔再靠消息里有没有 `401` 反推，一旦有人改了
   * 错误消息的措辞，判定就静默失效了。
   */
  function probe(
    serverAddress: string,
    hasCredentials: boolean
  ):
    | { kind: 'unreachable'; message: string }
    | { kind: 'denied'; message: string }
    | { kind: 'ok'; authenticated: boolean; message: string } {
    const address = serverAddress.trim().toLowerCase()

    if (address.startsWith('unreachable')) {
      return { kind: 'unreachable', message: 'dial tcp: connection refused' }
    }

    if (address.startsWith('denied')) {
      return { kind: 'denied', message: '401 Unauthorized from /v2/' }
    }

    if (address.startsWith('open')) {
      return { kind: 'ok', authenticated: false, message: '匿名可访问（该仓库未要求认证）' }
    }

    return hasCredentials
      ? { kind: 'ok', authenticated: true, message: '已连上 /v2/ 并通过认证' }
      : { kind: 'ok', authenticated: false, message: '匿名可访问（未提供账号）' }
  }

  /** 探测结果 → 响应体（两个 `test` 分支共用，避免两处各写一套映射）。 */
  function probeResponse(result: ReturnType<typeof probe>) {
    if (result.kind === 'unreachable') {
      return HttpResponse.json(fail(ErrorCode.RepositoryUnreachable, result.message), {
        status: httpStatusOf(ErrorCode.RepositoryUnreachable)
      })
    }

    if (result.kind === 'denied') {
      return HttpResponse.json(fail(ErrorCode.RepositoryAuthFailed, result.message), {
        status: httpStatusOf(ErrorCode.RepositoryAuthFailed)
      })
    }

    return HttpResponse.json(
      ok({ reachable: true, authenticated: result.authenticated, message: result.message })
    )
  }

  return [
    http.post('*/api/v1/admin/repositories/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)
      const search = String(body.search ?? '').trim().toLowerCase()
      const all = repositoriesFor(scenario())

      // 按别名或地址模糊过滤且不分大小写（后端在内存里做，前端不该再过滤一遍）
      const filtered =
        search.length === 0
          ? all
          : all.filter(
              (item) =>
                item.name.toLowerCase().includes(search) ||
                item.serverAddress.toLowerCase().includes(search)
            )

      return HttpResponse.json(ok(filtered))
    }),

    http.post('*/api/v1/admin/repositories/add', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const serverAddress = String(body.serverAddress ?? '').trim()
      const username = String(body.username ?? '').trim()
      const password = String(body.password ?? '')

      if (name.length === 0 || serverAddress.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '别名与服务地址均为必填'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 账号与口令必须同时给或同时不给（后端两个方向都校验）
      if ((username.length > 0) !== (password.length > 0)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '账号与口令必须同时填写，或都不填'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const all = repositoriesFor(scenario())

      // 别名唯一（COLLATE NOCASE）
      if (all.some((item) => item.name.toLowerCase() === name.toLowerCase())) {
        return HttpResponse.json(
          fail(ErrorCode.RepositoryNameAlreadyExists, `别名「${name}」已被占用`),
          { status: httpStatusOf(ErrorCode.RepositoryNameAlreadyExists) }
        )
      }

      // 地址唯一：同址两条配置会让「推送用哪条凭据」变成随机行为
      if (all.some((item) => item.serverAddress.toLowerCase() === serverAddress.toLowerCase())) {
        return HttpResponse.json(
          fail(ErrorCode.RepositoryAddressAlreadyExists, `地址「${serverAddress}」已被占用`),
          { status: httpStatusOf(ErrorCode.RepositoryAddressAlreadyExists) }
        )
      }

      const created: RepositorySummary = {
        id: crypto.randomUUID(),
        name,
        // 规范化：去 scheme、去尾斜杠、小写（与后端 RepositoryServerAddress 一致）
        serverAddress: serverAddress
          .replace(/^https?:\/\//i, '')
          .replace(/\/+$/, '')
          .toLowerCase(),
        username,
        hasCredentials: username.length > 0,
        description: String(body.description ?? '').trim() || null,
        createdAt: new Date().toISOString(),
        lastModifiedAt: null
      }

      addedInSession.push(created)

      // ⚠️ 响应里**没有** password / encryptedPassword：与后端一致
      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/repositories/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.id ?? '').trim()

      if (id.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'id 不能为空'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const addedIndex = addedInSession.findIndex((item) => item.id === id)
      const existsInFixtures = MOCK_REPOSITORIES_DEFAULT.some((item) => item.id === id)

      if (addedIndex < 0 && !existsInFixtures) {
        return HttpResponse.json(fail(ErrorCode.RepositoryNotFound, '仓库配置不存在'), {
          status: httpStatusOf(ErrorCode.RepositoryNotFound)
        })
      }

      if (addedIndex >= 0) addedInSession.splice(addedIndex, 1)
      else removedInSession.add(id)

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/admin/repositories/test', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.id ?? '').trim()

      // 给了 id → 测已保存的配置；口令由服务端解密，不经过这里
      if (id.length > 0) {
        const saved = repositoriesFor(scenario()).find((item) => item.id === id)

        if (!saved) {
          return HttpResponse.json(fail(ErrorCode.RepositoryNotFound, '仓库配置不存在'), {
            status: httpStatusOf(ErrorCode.RepositoryNotFound)
          })
        }

        // 不可达 / 认证被拒都以**失败响应**返回（与后端一致：这不是「成功但结果为否」）
        return probeResponse(probe(saved.serverAddress, saved.hasCredentials))
      }

      // 未给 id → 测尚未保存的输入（「先试再存」动线）
      const serverAddress = String(body.serverAddress ?? '').trim()
      const username = String(body.username ?? '').trim()

      if (serverAddress.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'serverAddress 不能为空'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      return probeResponse(probe(serverAddress, username.length > 0))
    })
  ]
}
