import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, hostsFor, type MockScenario } from '../scenarios'
import { MOCK_HOST_GROUPS } from '../data/hosts'
import type { AdminHostSummary, HostGroup } from '@/types/api'

/**
 * 主机相关端点 mock。
 *
 * 覆盖四类端点：
 *  1. `POST /api/v1/hosts/list`              —— **当前用户可见**的主机（非 admin 也要能用）
 *  2. `POST /api/v1/admin/hosts/list`        —— 主机管理页专用（admin）
 *  3. `POST /api/v1/hosts/{hostId}/overview` —— 聚合 `version` / `counts`（较重，单独一个）
 *  4. `POST /api/v1/host-groups/*` 与 `admin/host-groups/*` —— 分组读与写（0.0.13）
 *
 * 为什么列表与聚合要分开：列表会对每台主机做**轻量 ping** 取连通状态（便宜），
 * 而 `version` / `counts` 需要完整调用 Docker API（贵）。若都塞进列表，
 * 主机一多就会明显变慢甚至超时。分开后总览页一个请求就能画出行与状态。
 *
 * 复刻后端 `CurrentHostMiddleware` / `HostGroupService` 的关键分支：
 *  - 主机不存在 → `1300` / 404；`enabled = false` → `1302` / 400
 *  - 分组名重复 → `1304` / 400；分组不存在 → `1305` / 404
 *  - 「测试连通性」连不上是 **200 + data=false**，不是错误响应
 *  - **删除分组不删主机**：组内主机回到未分组
 */

/** 从请求路径里取 hostId（路径形如 `/api/v1/hosts/{hostId}/overview`）。 */
function hostIdFrom(request: Request): string {
  const m = new URL(request.url).pathname.match(/\/hosts\/([^/]+)\/overview$/)
  return m ? decodeURIComponent(m[1]) : ''
}

/** 从路径里取某个动作前的段（`/hosts/{hostId}/group`、`/host-groups/{groupId}/delete` 共用）。 */
function segmentBefore(request: Request, action: string): string {
  // ⚠️ 结尾不能多写一个 `/`：那会让模式要求「路径结束后还有一个斜杠」，永远匹配不上
  const m = new URL(request.url).pathname.match(new RegExp(`/([^/]+)/${action}$`))

  return m ? decodeURIComponent(m[1]) : ''
}

/**
 * 会话内新建的主机（管理端点返回**另一种形状**：`id` 是主键、状态是 `reachable`）。
 *
 * 与容器 / 镜像同一个理由：`create` 只返回结果的话，紧接着的列表看不到它，
 * 界面表现成「创建成功但列表里没有」。
 */
const createdHosts: AdminHostSummary[] = []
const removedHosts = new Set<string>()

/** 会话内新建的分组。 */
const createdGroups: HostGroup[] = []
const removedGroups = new Set<string>()

/** 会话内的分组改名 / 改描述结果（分组主键 → 覆盖后的分组）。 */
const updatedGroups = new Map<string, HostGroup>()

/** 会话内的主机归属调整（主机名 → 分组主键 | null）。 */
const groupAssignments = new Map<string, string | null>()

/** 清空会话内的主机与分组改动（供测试隔离用）。 */
export function resetSessionHosts(): void {
  createdHosts.length = 0
  removedHosts.clear()
  createdGroups.length = 0
  removedGroups.clear()
  updatedGroups.clear()
  groupAssignments.clear()
}

export function hostHandlers(scenario: () => MockScenario) {
  /** 取当前场景下的主机，并按 id 查出单台。 */
  const findHost = (id: string) => hostsFor(scenario()).find((h) => h.id === id)

  /** 可见主机列表（叠加会话内的归属调整）。 */
  const visibleHosts = () =>
    hostsFor(scenario()).map((host) =>
      groupAssignments.has(host.id)
        ? { ...host, groupId: groupAssignments.get(host.id) ?? undefined }
        : host
    )

  /** 主机列表（两个列表端点共用）。 */
  const listResponse = async () => {
    await delay(delayFor(scenario()))

    // error 场景：列表整体失败，用于验证页面的错误态 + 重试
    if (scenario() === 'error') {
      return HttpResponse.json(fail(ErrorCode.InternalError), {
        status: httpStatusOf(ErrorCode.InternalError)
      })
    }

    return HttpResponse.json(ok(visibleHosts()))
  }

  /** 管理端点的主机（主键 id + reachable，形状与展示列表不同）。 */
  const adminHosts = (): AdminHostSummary[] =>
    [
      ...visibleHosts().map((host) => ({
        id: `00000000-0000-0000-0000-00000000000${
          host.id === 'local' ? 1 : host.id === 'prod-01' ? 2 : 3
        }`,
        name: host.name,
        type: host.type,
        endpoint: host.endpoint,
        enabled: host.enabled,
        // 未探测用 null 表示：把它当 false 会让界面显示成「离线」，把没测当成故障
        reachable: host.status === undefined ? null : host.status === 'online',
        authKind: host.type === 'tcp' ? ('basic' as const) : ('none' as const),
        groupId: host.groupId ?? null
      })),
      ...createdHosts
    ].filter((host) => !removedHosts.has(host.id))

  /** 全部分组（叠加会话内的增删改）。 */
  const groups = (): HostGroup[] =>
    [...MOCK_HOST_GROUPS, ...createdGroups]
      .filter((group) => !removedGroups.has(group.id))
      .map((group) => updatedGroups.get(group.id) ?? group)

  /** 分组列表的 `hostCount` 按当前主机归属实时算：否则界面上的「N 台」会与主机列表互相矛盾。 */
  const groupsWithCounts = (): HostGroup[] => {
    const hosts = visibleHosts()

    return groups().map((group) => ({
      ...group,
      hostCount: hosts.filter((host) => host.groupId === group.id).length
    }))
  }

  /** 内置分组的改动要与夹具隔离（夹具是模块级常量，原地改会污染后续用例）。 */
  const storeGroupUpdate = (group: HostGroup): void => {
    const createdIndex = createdGroups.findIndex((item) => item.id === group.id)

    if (createdIndex >= 0) createdGroups[createdIndex] = group
    else updatedGroups.set(group.id, group)
  }

  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  const groupListResponse = async () => {
    await delay(delayFor(scenario()))

    if (scenario() === 'error') {
      return HttpResponse.json(fail(ErrorCode.InternalError), {
        status: httpStatusOf(ErrorCode.InternalError)
      })
    }

    return HttpResponse.json(ok(groupsWithCounts()))
  }

  return [
    // ===== 当前用户可见主机（总览页 / 侧栏 HostSelector 都用它）=====
    http.post('*/api/v1/hosts/list', listResponse),

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
    }),

    // ===== 分组：读（所有已登录用户，总览页要用）=====
    http.post('*/api/v1/host-groups/list', groupListResponse),

    // ===== 分组：管理（admin）=====
    http.post('*/api/v1/admin/host-groups/list', groupListResponse),

    http.post('*/api/v1/admin/host-groups/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()

      if (name.length === 0 || name.length > 32) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '分组名长度须在 1-32 之间'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // ⚠️ 必须与服务端一致地判重，否则界面会在「创建成功」与「列表里已有同名」之间矛盾
      if (groups().some((group) => group.name.toLowerCase() === name.toLowerCase())) {
        return HttpResponse.json(fail(ErrorCode.GroupNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.GroupNameAlreadyExists)
        })
      }

      const created: HostGroup = {
        id: `00000000-0000-0000-0000-0000000000${(96 + createdGroups.length).toString(16)}`,
        name,
        description: String(body.description ?? '').trim() || null,
        sortOrder: Number(body.sortOrder ?? 0) || 0,
        hostCount: 0
      }

      createdGroups.push(created)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/host-groups/:groupId/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const groupId = segmentBefore(request, 'update')
      const body = await readBody(request)
      const target = groups().find((group) => group.id === groupId)

      if (target === undefined) {
        return HttpResponse.json(fail(ErrorCode.GroupNotFound), {
          status: httpStatusOf(ErrorCode.GroupNotFound)
        })
      }

      const name = body.name === undefined ? target.name : String(body.name).trim()

      // 判重要**排除自己**：否则「只改描述」的保存会被自己的旧名字挡住
      const duplicate = groups().some(
        (group) => group.id !== target.id && group.name.toLowerCase() === name.toLowerCase()
      )

      if (duplicate) {
        return HttpResponse.json(fail(ErrorCode.GroupNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.GroupNameAlreadyExists)
        })
      }

      const updated: HostGroup = {
        ...target,
        name,
        // 描述传空串表示清空（JSON 分不清「没给」与「给了 null」，用空串表达清空）
        description:
          body.description === undefined
            ? target.description
            : String(body.description).trim() || null,
        sortOrder: body.sortOrder === undefined ? target.sortOrder : Number(body.sortOrder) || 0
      }

      storeGroupUpdate(updated)

      return HttpResponse.json(ok(updated))
    }),

    http.post('*/api/v1/admin/host-groups/:groupId/delete', async ({ request }) => {
      await delay(delayFor(scenario()))

      const groupId = segmentBefore(request, 'delete')
      const target = groups().find((group) => group.id === groupId)

      if (target === undefined) {
        return HttpResponse.json(fail(ErrorCode.GroupNotFound), {
          status: httpStatusOf(ErrorCode.GroupNotFound)
        })
      }

      // ⚠️ 关键语义：删除分组**不**删主机，组内主机回到未分组。
      // 不复刻它，界面上「已把 N 台主机移出该分组」的提示就永远测不到。
      const members = visibleHosts().filter((host) => host.groupId === groupId)

      for (const member of members) {
        groupAssignments.set(member.id, null)
      }

      removedGroups.add(groupId)
      createdGroups.splice(
        createdGroups.findIndex((group) => group.id === groupId),
        1
      )

      return HttpResponse.json(ok(members.length))
    }),

    // ===== 主机管理（admin）=====
    http.post('*/api/v1/admin/hosts/list', async () => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      return HttpResponse.json(ok(adminHosts()))
    }),

    http.post('*/api/v1/admin/hosts/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const endpoint = String(body.endpoint ?? '').trim()

      if (name.length === 0 || endpoint.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      if (adminHosts().some((host) => host.name.toLowerCase() === name.toLowerCase())) {
        return HttpResponse.json(fail(ErrorCode.HostNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.HostNameAlreadyExists)
        })
      }

      const created: AdminHostSummary = {
        id: `00000000-0000-0000-0000-0000000000${(48 + createdHosts.length).toString(16)}`,
        name,
        type: body.type === 'tcp' ? 'tcp' : 'socket',
        endpoint,
        enabled: true,
        // 创建**不探活**（写操作不该被网络超时拖住）：用 null 表示「未探测」，
        // 用 false 会让界面显示成「离线」，把没测当成故障
        reachable: null,
        authKind: body.tlsConfig ? 'tls' : body.basicAuth ? 'basic' : 'none',
        groupId: typeof body.groupId === 'string' ? body.groupId : null
      }

      createdHosts.push(created)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/hosts/:hostId/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const hostId = segmentBefore(request, 'update')
      const body = await readBody(request)
      const target = adminHosts().find((host) => host.name === hostId)

      if (target === undefined) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      const updated: AdminHostSummary = {
        ...target,
        endpoint: typeof body.endpoint === 'string' ? body.endpoint : target.endpoint,
        enabled: typeof body.enabled === 'boolean' ? body.enabled : target.enabled,
        // 更新与创建一样不探活：把「没测」如实标成 null
        reachable: null,
        authKind:
          body.clearCredentials === true
            ? 'none'
            : body.tlsConfig
              ? 'tls'
              : body.basicAuth
                ? 'basic'
                : target.authKind
      }

      const index = createdHosts.findIndex((host) => host.id === target.id)
      if (index >= 0) createdHosts[index] = updated

      return HttpResponse.json(ok(updated))
    }),

    http.post('*/api/v1/admin/hosts/:hostId/delete', async ({ request }) => {
      await delay(delayFor(scenario()))

      const hostId = segmentBefore(request, 'delete')
      const target = adminHosts().find((host) => host.name === hostId)

      if (target === undefined) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      removedHosts.add(target.id)
      createdHosts.splice(
        createdHosts.findIndex((host) => host.id === target.id),
        1
      )

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/admin/hosts/test', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const endpoint = String(body.endpoint ?? '').trim()

      if (endpoint.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 端点里含 `unreachable` → 连不上，但**仍是 200 + data=false**（不是错误响应）：
      // 界面要把「连不上」显示成一个结论，而不是一个失败弹窗。
      // 这条不复刻，两种形态的区分就永远测不到。
      // 用「包含」而不是「前缀」：真实端点总是以 tcp:// 之类开头，前缀判据在界面上根本触发不了。
      return HttpResponse.json(ok(!endpoint.includes('unreachable')))
    }),

    http.post('*/api/v1/admin/hosts/:hostId/group', async ({ request }) => {
      await delay(delayFor(scenario()))

      const hostId = segmentBefore(request, 'group')
      const body = await readBody(request)
      const host = visibleHosts().find((item) => item.id === hostId)

      if (host === undefined) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      const groupId = body.groupId == null ? null : String(body.groupId)

      // 外键拦不住「指向一个已软删除的分组」，因此必须显式校验目标存在
      if (groupId !== null && !groups().some((group) => group.id === groupId)) {
        return HttpResponse.json(fail(ErrorCode.GroupNotFound), {
          status: httpStatusOf(ErrorCode.GroupNotFound)
        })
      }

      groupAssignments.set(hostId, groupId)

      return HttpResponse.json(ok(null))
    })
  ]
}
