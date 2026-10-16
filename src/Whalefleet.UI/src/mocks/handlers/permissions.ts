import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, hostsFor, type MockScenario } from '../scenarios'
import {
  isKnownPermission,
  MOCK_PERMISSION_DEFINITIONS,
  MOCK_PERMISSION_NAMES,
  MOCK_ROLE_ASSIGNMENTS_DEFAULT,
  MOCK_ROLES_DEFAULT
} from '../data/permissions'
import { sessionUsers } from './users'
import type { RoleAssignmentSummary, RoleSummary } from '@/types/api'

/**
 * 角色与角色分配端点 mock（对应后端 0.1.0 的三组端点）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 *
 * 1. **`1512`：角色仍在被分配时不能删** —— 后端不级联撤销（那会悄悄收回
 *    没人要求收回的访问权）。不模拟这一条，「删除一定会成功」的错误心智模型就建立起来了。
 * 2. **`1513`：内置角色的权限集不可改，也不可删** —— 但**可读信息可以改**
 *    （`Role.ChangeDescription` 不判内置），这个不对称必须复刻：
 *    一律拒绝会让「编辑内置角色的名字」这条正常路径在走查时变成 400。
 * 3. **`1515`：撤销一条不存在的分配** —— 与「分配已存在时重复分配返回同一条」相对，
 *    这两条构成分配表的完整语义；只做一半会让界面无从判断「该不该发撤销请求」。
 * 4. **`1511`：角色标识重复** —— 判重必须是**不区分大小写**的
 *    （后端唯一索引 `COLLATE NOCASE`，且 `Role.Create` 会把名字转小写）。
 * 5. **分配是 `(用户, 角色, 作用域)` 三元组且幂等** —— 重复分配返回**同一条**记录，
 *    不换主键；否则界面刷新后会看到一条 id 不同的「新」记录。
 * 6. **主机视角的 `list` 不含全局分配** —— 它对所有主机生效、无法归属到某一台。
 * 7. **角色权限名白名单**：`root` / `ALL` / 空串一律 1000，且**校验在写入之前**。
 * 8. **默认角色全库至多一个**：`set-default` 在同一个「事务」里清掉旧的默认标记。
 *    后端是**单向**的（没有 `isDefault` 字段），因此这里也拒绝用请求体取消默认。
 *
 * ## 会话态
 *
 * 全部改动存在模块级结构里，`resetSessionRoles()` 负责清空（测试隔离用）。
 * 会话内的新建角色 / 分配会**真实影响后续读取**——否则「创建成功但列表里没有」
 * 这种最典型的 mock 断层会让界面问题被掩盖。
 */

/* ==================================================================
 * 会话态
 * ================================================================== */

/** 会话内新建的角色。 */
const createdRoles: RoleSummary[] = []

/** 会话内对已有角色的覆盖（改信息 / 改权限 / 改默认）。 */
const roleOverrides = new Map<string, RoleSummary>()

/** 会话内被删除的角色主键。 */
const removedRoleIds = new Set<string>()

/** 会话内新建的分配。 */
const createdAssignments: RoleAssignmentSummary[] = []

/** 会话内被撤销的分配主键（软删除）。 */
const revokedAssignmentIds = new Set<string>()

/** 清空会话内的角色与分配改动（供测试隔离用）。 */
export function resetSessionRoles(): void {
  createdRoles.length = 0
  roleOverrides.clear()
  removedRoleIds.clear()
  createdAssignments.length = 0
  revokedAssignmentIds.clear()
}

/* ==================================================================
 * 读取视图
 * ================================================================== */

export function roleHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      // 有些端点没有请求体（`permissions` / 用户视角的 `list`），空体不是错误
      return {}
    }
  }

  /** 当前生效的分配（夹具 + 会话新建 − 会话撤销）。 */
  function assignmentsFor(): RoleAssignmentSummary[] {
    return [...MOCK_ROLE_ASSIGNMENTS_DEFAULT, ...createdAssignments].filter(
      (assignment) => !revokedAssignmentIds.has(assignment.id)
    )
  }

  /**
   * 当前生效的角色。
   *
   * `assignmentCount` **每次重算**而不是读夹具：删掉一个角色前界面要看到「还有几个人在用」，
   * 而会话里刚分配出去的次数必须立刻反映出来，否则「仍有分配不能删」这条
   * 会在界面上表现为「后端莫名拒绝」。
   */
  function rolesFor(): RoleSummary[] {
    const assignments = assignmentsFor()

    return [...MOCK_ROLES_DEFAULT, ...createdRoles]
      .filter((role) => !removedRoleIds.has(role.id))
      .map((role) => {
        const current = roleOverrides.get(role.id) ?? role

        return {
          ...current,
          assignmentCount: assignments.filter((item) => item.roleId === current.id).length
        }
      })
  }

  /** 目标用户是否存在；mock 只认用户夹具与本次会话新建的。 */
  function findUser(userId: string): { id: string; username: string } | undefined {
    return sessionUsers().find((user) => user.id === userId)
  }

  /** 主机名是否存在于当前场景；不存在时分配端点一律 1300。 */
  function hostExists(hostName: string): boolean {
    return hostsFor(scenario()).some((host) => host.name === hostName)
  }

  /** 按主键或标识定位角色（不区分大小写）。 */
  function resolveRole(body: Record<string, unknown>): RoleSummary | undefined {
    const roleId = typeof body.roleId === 'string' ? body.roleId.trim() : ''
    if (roleId.length > 0) return rolesFor().find((role) => role.id === roleId)

    const roleName = typeof body.role === 'string' ? body.role.trim().toLowerCase() : ''
    if (roleName.length > 0) return rolesFor().find((role) => role.name === roleName)

    return undefined
  }

  /** 请求里到底给没给定位条件——没给是 1000，给了但查不到才是 1510。 */
  function hasSelector(body: Record<string, unknown>): boolean {
    const roleId = typeof body.roleId === 'string' ? body.roleId.trim() : ''
    const roleName = typeof body.role === 'string' ? body.role.trim() : ''

    return roleId.length > 0 || roleName.length > 0
  }

  function overrideRole(role: RoleSummary): RoleSummary {
    roleOverrides.set(role.id, role)

    return role
  }

  function summaryOf(body: Record<string, unknown>): RoleSummary | undefined {
    const role = resolveRole(body)

    return role === undefined ? undefined : (roleOverrides.get(role.id) ?? role)
  }

  const notFound = () =>
    HttpResponse.json(fail(ErrorCode.RoleNotFound), {
      status: httpStatusOf(ErrorCode.RoleNotFound)
    })

  return [
    /* ================= 角色本身 ================= */

    http.post('*/api/v1/admin/roles/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)
      // `customOnly` 省略即全部：分配场景要能选到内置角色
      const onlyCustom = body.customOnly === true
      const roles = rolesFor()

      return HttpResponse.json(ok(onlyCustom ? roles.filter((role) => !role.isBuiltIn) : roles))
    }),

    http.post('*/api/v1/admin/roles/permissions', async () => {
      await delay(delayFor(scenario()))

      return HttpResponse.json(ok(MOCK_PERMISSION_DEFINITIONS))
    }),

    http.post('*/api/v1/admin/roles/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '')
        .trim()
        .toLowerCase()

      // 与后端 `Role.NormalizeName` 同规则：2–32 位，仅 ASCII 字母数字与 . _ -
      if (name.length < 2 || name.length > 32 || !/^[a-z0-9._-]+$/.test(name)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '角色名须为 2–32 位，仅允许字母、数字与 . _ -'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      // ⚠️ 判重必须不区分大小写：后端唯一索引带 COLLATE NOCASE
      if (rolesFor().some((role) => role.name === name)) {
        return HttpResponse.json(fail(ErrorCode.RoleNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.RoleNameAlreadyExists)
        })
      }

      // 校验必须在写入之前：非法权限名不允许留下一条「待修正」的角色
      let permissions: string[]
      try {
        permissions = normalizePermissions(body.permissions, true)
      } catch (issue) {
        return issue as HttpResponse
      }

      const created: RoleSummary = {
        id: `00000000-0000-0000-0000-0000000000${(208 + createdRoles.length).toString(16)}`,
        name,
        displayName: String(body.displayName ?? '').trim() || name,
        description: String(body.description ?? '').trim() || null,
        isBuiltIn: false,
        isDefault: false,
        permissions,
        assignmentCount: 0
      }

      createdRoles.push(created)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/roles/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)

      if (!hasSelector(body)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const role = summaryOf(body)
      if (role === undefined) return notFound()

      // 角色标识不可改；内置角色的**可读信息可以改**（与后端一致——受保护的是权限集与删除）
      const updated = overrideRole({
        ...role,
        displayName: String(body.displayName ?? '').trim() || role.name,
        description:
          body.description === undefined || body.description === null
            ? null
            : String(body.description).trim() || null
      })

      return HttpResponse.json(ok(updated))
    }),

    http.post('*/api/v1/admin/roles/set-permissions', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)

      if (!hasSelector(body)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const role = summaryOf(body)
      if (role === undefined) return notFound()

      // 内置角色的权限集随迁移固定：一个能被清空权限集的 operator
      // 已经不是它的名字所声称的那个角色
      if (role.isBuiltIn) {
        return HttpResponse.json(fail(ErrorCode.RoleProtected), {
          status: httpStatusOf(ErrorCode.RoleProtected)
        })
      }

      // 空数组是合法的（「收回这个角色的全部权限」）
      let permissions: string[]
      try {
        permissions = normalizePermissions(body.permissions, true)
      } catch (issue) {
        return issue as HttpResponse
      }

      return HttpResponse.json(ok(overrideRole({ ...role, permissions })))
    }),

    http.post('*/api/v1/admin/roles/set-default', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)

      if (!hasSelector(body)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const target = summaryOf(body)
      if (target === undefined) return notFound()

      // 全库至多一个默认角色：先清掉旧标记再设新的（后端在同一个事务里做）
      for (const role of rolesFor()) {
        if (role.id !== target.id && role.isDefault) {
          overrideRole({ ...role, isDefault: false })
        }
      }

      return HttpResponse.json(ok(overrideRole({ ...target, isDefault: true })))
    }),

    http.post('*/api/v1/admin/roles/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)

      if (!hasSelector(body)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const role = rolesFor().find(
        (item) =>
          item.id === (typeof body.roleId === 'string' ? body.roleId.trim() : '') ||
          item.name === (typeof body.role === 'string' ? body.role.trim().toLowerCase() : '')
      )

      if (role === undefined) return notFound()

      if (role.isBuiltIn) {
        return HttpResponse.json(fail(ErrorCode.RoleProtected), {
          status: httpStatusOf(ErrorCode.RoleProtected)
        })
      }

      // ⚠️ 不级联撤销：报这个错是把决定权交还管理员
      if (role.assignmentCount > 0) {
        return HttpResponse.json(fail(ErrorCode.RoleInUse), {
          status: httpStatusOf(ErrorCode.RoleInUse)
        })
      }

      removedRoleIds.add(role.id)
      roleOverrides.delete(role.id)

      const createdIndex = createdRoles.findIndex((item) => item.id === role.id)
      if (createdIndex >= 0) createdRoles.splice(createdIndex, 1)

      return HttpResponse.json(ok(null))
    }),

    /* ================= 角色分配：主机视角 ================= */

    http.post('*/api/v1/admin/hosts/:hostId/roles/list', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const hostName = String(params.hostId)
      if (!hostExists(hostName)) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      const body = await readBody(request)
      const rawUserId = body.userId

      // 主机分配（不含全局分配）——全局分配对所有主机生效、无法归属到某一台
      let rows = assignmentsFor().filter((item) => item.hostName === hostName)

      if (rawUserId !== undefined && rawUserId !== null) {
        const userId = String(rawUserId)

        // 先确认用户存在再过滤：给一个不存在的 userId 返回空列表，
        // 会让界面把「这个人没有分配」与「查错了人」渲染成同一个空态
        const user = findUser(userId)
        if (user === undefined) {
          return HttpResponse.json(fail(ErrorCode.UserNotFound), {
            status: httpStatusOf(ErrorCode.UserNotFound)
          })
        }

        rows = rows.filter((item) => item.userId === user.id)
      }

      return HttpResponse.json(ok(rows))
    }),

    http.post('*/api/v1/admin/hosts/:hostId/roles/assign', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const hostName = String(params.hostId)
      if (!hostExists(hostName)) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      const body = await readBody(request)
      const userId = String(body.userId ?? '')
      const user = findUser(userId)

      if (user === undefined) {
        return HttpResponse.json(fail(ErrorCode.UserNotFound), {
          status: httpStatusOf(ErrorCode.UserNotFound)
        })
      }

      const role = resolveRole(body)
      if (role === undefined) return notFound()

      // 幂等：同一 (用户, 角色, 本主机) 再次分配返回**同一条**记录
      const existing = assignmentsFor().find(
        (item) => item.userId === user.id && item.roleId === role.id && item.hostName === hostName
      )
      if (existing !== undefined) return HttpResponse.json(ok(existing))

      const created: RoleAssignmentSummary = {
        id: `00000000-0000-0000-0000-0000000000${(288 + createdAssignments.length).toString(16)}`,
        userId: user.id,
        username: user.username,
        roleId: role.id,
        role: role.name,
        roleDisplayName: role.displayName,
        hostId: hostGuidOf(hostName),
        hostName,
        lastModificationTime: null
      }

      createdAssignments.push(created)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/hosts/:hostId/roles/revoke', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const hostName = String(params.hostId)
      if (!hostExists(hostName)) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      const body = await readBody(request)
      const userId = String(body.userId ?? '')
      const role = resolveRole(body)

      if (role === undefined) return notFound()

      const existing = assignmentsFor().find(
        (item) => item.userId === userId && item.roleId === role.id && item.hostName === hostName
      )

      // 撤销一条本就不存在的分配 → 1515（与「重复分配幂等」相对）
      if (existing === undefined) {
        return HttpResponse.json(fail(ErrorCode.RoleAssignmentNotFound), {
          status: httpStatusOf(ErrorCode.RoleAssignmentNotFound)
        })
      }

      revokedAssignmentIds.add(existing.id)

      return HttpResponse.json(ok(null))
    }),

    /* ================= 角色分配：用户视角 ================= */

    http.post('*/api/v1/admin/users/:userId/roles/list', async ({ params }) => {
      await delay(delayFor(scenario()))

      const userId = String(params.userId)
      const user = findUser(userId)

      if (user === undefined) {
        return HttpResponse.json(fail(ErrorCode.UserNotFound), {
          status: httpStatusOf(ErrorCode.UserNotFound)
        })
      }

      // 全局 + 各主机：这个视角回答的是「这个人拥有什么」
      return HttpResponse.json(ok(assignmentsFor().filter((item) => item.userId === user.id)))
    }),

    http.post('*/api/v1/admin/users/:userId/roles/assign', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const user = findUser(String(params.userId))
      if (user === undefined) {
        return HttpResponse.json(fail(ErrorCode.UserNotFound), {
          status: httpStatusOf(ErrorCode.UserNotFound)
        })
      }

      const body = await readBody(request)
      const role = resolveRole(body)
      if (role === undefined) return notFound()

      // 省略 host 即**全局分配**——这与主机视角的端点不同，那条路径的作用域固定在 URL 上
      const rawHost = typeof body.host === 'string' ? body.host.trim() : ''
      const hostName = rawHost.length > 0 ? rawHost : null

      if (hostName !== null && !hostExists(hostName)) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      const existing = assignmentsFor().find(
        (item) =>
          item.userId === user.id && item.roleId === role.id && item.hostName === hostName
      )
      if (existing !== undefined) return HttpResponse.json(ok(existing))

      const created: RoleAssignmentSummary = {
        id: `00000000-0000-0000-0000-0000000000${(304 + createdAssignments.length).toString(16)}`,
        userId: user.id,
        username: user.username,
        roleId: role.id,
        role: role.name,
        roleDisplayName: role.displayName,
        hostId: hostName === null ? null : hostGuidOf(hostName),
        hostName,
        lastModificationTime: null
      }

      createdAssignments.push(created)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/admin/users/:userId/roles/revoke', async ({ request, params }) => {
      await delay(delayFor(scenario()))

      const userId = String(params.userId)
      const body = await readBody(request)
      const role = resolveRole(body)

      if (role === undefined) return notFound()

      const rawHost = typeof body.host === 'string' ? body.host.trim() : ''
      const hostName = rawHost.length > 0 ? rawHost : null

      const existing = assignmentsFor().find(
        (item) => item.userId === userId && item.roleId === role.id && item.hostName === hostName
      )

      if (existing === undefined) {
        return HttpResponse.json(fail(ErrorCode.RoleAssignmentNotFound), {
          status: httpStatusOf(ErrorCode.RoleAssignmentNotFound)
        })
      }

      revokedAssignmentIds.add(existing.id)

      return HttpResponse.json(ok(null))
    })
  ]
}

/* ==================================================================
 * 辅助
 * ================================================================== */

/**
 * 主机名 → 主键。
 *
 * 规则与 `handlers/hosts.ts` 的 `adminHosts()` **完全一致**（local→1、prod-01→2、其余→3）：
 * 契约里 `hostId`（GUID）与 `hostName` 成对出现，界面按名字渲染、按 GUID 回填。
 * 两边各算一套的话，走查时会看到「同一条分配在两个接口里 hostId 不一样」这种幽灵问题。
 */
function hostGuidOf(hostName: string): string {
  const suffix = hostName === 'local' ? 1 : hostName === 'prod-01' ? 2 : 3

  return `00000000-0000-0000-0000-00000000000${suffix}`
}

/**
 * 校验并规范化权限数组。
 *
 * 与后端 `PermissionNames.Normalize` 同口径：转小写、去重、**按目录顺序排列**。
 * 顺序归一不是洁癖——数据库的 `SELECT` 不承诺排序，不归一的话同一个角色
 * 两次读出来的权限顺序可能不同，界面上的勾选框就会跟着抖。
 *
 * @returns 规范化后的权限名。
 * @throws 与后端 `PermissionNames.Normalize` 同一口径：
 *   **非数组 / 空串 / 空数组（且不允许）→ `1000`**，
 *   **非空但不在目录里的权限名 → `1514`**。
 *
 *   分成两个码不是讲究：客户端最容易犯的错就是拼错权限名
 *   （`"read"` 而不是 `"host.read"`、`"ALL"`、`"7"`），给它独立码，
 *   界面才能提示「请从权限目录里选」——见 `docs/api-spec.md`
 *   「未知权限名一律 1514/400，且不落库」。
 */
function normalizePermissions(raw: unknown, allowEmpty: boolean): string[] {
  if (raw === undefined || raw === null) {
    if (allowEmpty) return []
    throw mockValidation()
  }
  if (!Array.isArray(raw)) throw mockValidation()

  const set = new Set<string>()

  for (const item of raw) {
    if (typeof item !== 'string' || item.trim().length === 0) throw mockValidation()

    const name = item.trim().toLowerCase()
    if (!isKnownPermission(name)) {
      throw HttpResponse.json(fail(ErrorCode.PermissionUnknown), {
        status: httpStatusOf(ErrorCode.PermissionUnknown)
      })
    }

    set.add(name)
  }

  if (set.size === 0 && !allowEmpty) throw mockValidation()

  return MOCK_PERMISSION_NAMES.filter((name) => set.has(name))
}

/** `1000` 的失败响应（抛出版，让调用点统一 `catch` 后直接 return）。 */
function mockValidation(): never {
  throw HttpResponse.json(fail(ErrorCode.ValidationFailed), {
    status: httpStatusOf(ErrorCode.ValidationFailed)
  })
}
