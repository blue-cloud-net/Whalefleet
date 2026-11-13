import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import { MOCK_ROLES_DEFAULT } from '../data/permissions'
import { sessionUsers } from './users'
import type {
  TeamDetail,
  TeamMemberSummary,
  TeamRoleAssignmentSummary,
  TeamSummary,
  TeamSyncResult
} from '@/types/api'

/**
 * 团队端点 mock（0.13.0）。
 *
 * ## 复刻的关键语义
 * 1. **团队只收窄、不授予**：`members/add` 不改任何人的个人授权；
 *    只有 `sync` 才会把团队角色写成「个人授权」。界面上的「同步到成员」
 *    因此是**必需**的一步，而不是装饰。
 * 2. **团队名不区分大小写唯一** → 重复 `1203`；不存在 → `1204`。
 * 3. **角色授予幂等**：同一 (角色, 作用域) 重复授予返回同一条，不产生第二条。
 * 4. **撤销不存在的授权** → `1516`。
 */

interface MockTeamRoleAssignment {
  id: string
  roleId: string
  role: string
  hostId: string | null
  hostName: string | null
  containerId: string | null
}

interface MockTeam {
  id: string
  name: string
  displayName: string | null
  description: string | null
  creationTime: string
  memberIds: string[]
  roleAssignments: MockTeamRoleAssignment[]
}

/** 团队夹具：两个团队，覆盖「有成员 + 有角色」与「空团队」两种形态。 */
const initialTeams: MockTeam[] = [
  {
    id: '00000000-0000-0000-0000-0000000000c1',
    name: 'sre',
    displayName: 'SRE 组',
    description: '负责生产环境的稳定性',
    creationTime: '2026-10-01T02:00:00Z',
    memberIds: [
      '00000000-0000-0000-0000-0000000000b1',
      '00000000-0000-0000-0000-0000000000b2'
    ],
    roleAssignments: [
      {
        id: '00000000-0000-0000-0000-0000000000d1',
        roleId: '00000000-0000-0000-0000-0000000000a2',
        role: 'operator',
        hostId: null,
        hostName: 'local',
        containerId: null
      }
    ]
  },
  {
    id: '00000000-0000-0000-0000-0000000000c2',
    name: 'audit',
    displayName: '审计组',
    description: '只读与审计',
    creationTime: '2026-10-05T02:00:00Z',
    memberIds: [],
    roleAssignments: []
  }
]

let teams: MockTeam[] = structuredClone(initialTeams)

/** 本次会话里已被同步到成员个人授权的组合（用于幂等统计）。 */
const syncedAssignments = new Set<string>()

/** 清空会话内的团队改动（供测试隔离用）。 */
export function resetSessionTeams(): void {
  teams = structuredClone(initialTeams)
  syncedAssignments.clear()
}

/** 角色名 → 角色主键（找不到时给一个稳定占位，避免 mock 因自定义角色缺失而崩）。 */
function roleIdOf(roleName: string): string {
  return MOCK_ROLES_DEFAULT.find((role) => role.name === roleName)?.id ?? `role-${roleName}`
}

/** 角色名 → 可读名称。 */
function roleDisplayNameOf(roleName: string): string | null {
  return MOCK_ROLES_DEFAULT.find((role) => role.name === roleName)?.displayName ?? null
}

function summaryOf(team: MockTeam): TeamSummary {
  return {
    id: team.id,
    name: team.name,
    displayName: team.displayName,
    description: team.description,
    memberCount: team.memberIds.length,
    roleAssignmentCount: team.roleAssignments.length,
    creationTime: team.creationTime
  }
}

function membersOf(team: MockTeam): TeamMemberSummary[] {
  const users = sessionUsers()

  return team.memberIds
    .map((userId) => users.find((user) => user.id === userId))
    .filter((user): user is NonNullable<typeof user> => user !== undefined)
    .map((user) => ({
      userId: user.id,
      username: user.username,
      joinedAt: team.creationTime,
      isDisabled: user.isDisabled,
      mustChangePassword: user.mustChangePassword
    }))
}

function detailOf(team: MockTeam): TeamDetail {
  return {
    summary: summaryOf(team),
    members: membersOf(team),
    // ⚠️ 必须逐条映射：夹具里存的是「引用」形态，响应要的是带 `roleDisplayName` 的
    // 展示形态。直接返回原数组会让界面上的角色列显示成空白（类型检查才发现）。
    roleAssignments: team.roleAssignments.map(assignmentView)
  }
}

function assignmentView(assignment: MockTeamRoleAssignment): TeamRoleAssignmentSummary {
  return {
    id: assignment.id,
    roleId: assignment.roleId,
    role: assignment.role,
    roleDisplayName: roleDisplayNameOf(assignment.role),
    hostId: assignment.hostId,
    hostName: assignment.hostName,
    containerId: assignment.containerId
  }
}

export function teamHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  function findTeam(teamId: string): MockTeam | undefined {
    return teams.find((team) => team.id === teamId)
  }

  function notFound(): Response {
    return HttpResponse.json(fail(ErrorCode.TeamNotFound), {
      status: httpStatusOf(ErrorCode.TeamNotFound)
    })
  }

  return [
    http.post('*/api/v1/admin/teams/list', async ({ request }) => {
      await delay(delayFor(scenario()))
      const body = await readBody(request)
      const pageIndex = Math.max(Number(body.pageIndex ?? 0) || 0, 0)
      const pageSize = Math.min(Math.max(Number(body.pageSize ?? 20) || 20, 1), 100)
      const all = scenario() === 'empty' ? [] : teams

      return HttpResponse.json(
        ok({
          items: all.slice(pageIndex * pageSize, (pageIndex + 1) * pageSize).map(summaryOf),
          total: all.length,
          pageIndex,
          pageSize
        })
      )
    }),

    http.post('*/api/v1/admin/teams/:teamId/detail', async ({ params }) => {
      await delay(delayFor(scenario()))
      const team = findTeam(String(params.teamId ?? ''))
      return team === undefined ? notFound() : HttpResponse.json(ok(detailOf(team)))
    }),

    http.post('*/api/v1/admin/teams/create', async ({ request }) => {
      await delay(delayFor(scenario()))
      const body = await readBody(request)
      const name = String(body.name ?? '').trim()

      if (name.length < 2) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '团队标识至少 2 个字符'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 团队名不区分大小写唯一
      if (teams.some((team) => team.name.toLowerCase() === name.toLowerCase())) {
        return HttpResponse.json(fail(ErrorCode.TeamNameAlreadyExists), {
          status: httpStatusOf(ErrorCode.TeamNameAlreadyExists)
        })
      }

      const team: MockTeam = {
        id: `00000000-0000-0000-0000-0000000000c${teams.length + 3}`,
        name,
        displayName: String(body.displayName ?? '').trim() || null,
        description: String(body.description ?? '').trim() || null,
        creationTime: new Date().toISOString(),
        memberIds: [],
        roleAssignments: []
      }

      teams.push(team)
      return HttpResponse.json(ok(summaryOf(team)))
    }),

    http.post('*/api/v1/admin/teams/update', async ({ request }) => {
      await delay(delayFor(scenario()))
      const body = await readBody(request)
      const team = findTeam(String(body.teamId ?? ''))

      if (team === undefined) return notFound()

      team.displayName = String(body.displayName ?? '').trim() || null
      team.description = String(body.description ?? '').trim() || null

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/admin/teams/remove', async ({ request }) => {
      await delay(delayFor(scenario()))
      const body = await readBody(request)
      const index = teams.findIndex((team) => team.id === String(body.teamId ?? ''))

      if (index < 0) return notFound()

      teams.splice(index, 1)
      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/admin/teams/:teamId/members/add', async ({ request, params }) => {
      await delay(delayFor(scenario()))
      const team = findTeam(String(params.teamId ?? ''))
      if (team === undefined) return notFound()

      const body = await readBody(request)
      const userIds = Array.isArray(body.userIds) ? body.userIds.map(String) : []
      const known = new Set(sessionUsers().map((user) => user.id))

      if (userIds.some((id) => !known.has(id))) {
        return HttpResponse.json(fail(ErrorCode.UserNotFound), {
          status: httpStatusOf(ErrorCode.UserNotFound)
        })
      }

      let added = 0
      for (const userId of userIds) {
        if (!team.memberIds.includes(userId)) {
          team.memberIds.push(userId)
          added += 1
        }
      }

      // ⚠️ 刻意**不**写任何个人授权：团队只定权限上限，授予必须显式「同步」。
      return HttpResponse.json(ok(added))
    }),

    http.post('*/api/v1/admin/teams/:teamId/members/remove', async ({ request, params }) => {
      await delay(delayFor(scenario()))
      const team = findTeam(String(params.teamId ?? ''))
      if (team === undefined) return notFound()

      const body = await readBody(request)
      const userIds = Array.isArray(body.userIds) ? body.userIds.map(String) : []
      const before = team.memberIds.length

      team.memberIds = team.memberIds.filter((id) => !userIds.includes(id))

      return HttpResponse.json(ok(before - team.memberIds.length))
    }),

    http.post('*/api/v1/admin/teams/:teamId/roles/assign', async ({ request, params }) => {
      await delay(delayFor(scenario()))
      const team = findTeam(String(params.teamId ?? ''))
      if (team === undefined) return notFound()

      const body = await readBody(request)
      const role = String(body.role ?? '').trim()

      if (role.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'role 或 roleId 至少提供一个'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const host = String(body.host ?? '').trim()
      const containerId = String(body.containerId ?? '').trim()

      if (containerId.length > 0 && host.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '容器级授权必须同时指定主机'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const existing = team.roleAssignments.find(
        (assignment) =>
          assignment.role === role &&
          (assignment.hostName ?? '') === host &&
          (assignment.containerId ?? '') === containerId
      )

      if (existing !== undefined) {
        return HttpResponse.json(ok(assignmentView(existing)))
      }

      const assignment: MockTeamRoleAssignment = {
        id: `00000000-0000-0000-0000-0000000000d${team.roleAssignments.length + 2}`,
        roleId: roleIdOf(role),
        role,
        hostId: host.length > 0 ? `host-${host}` : null,
        hostName: host.length > 0 ? host : null,
        containerId: containerId.length > 0 ? containerId : null
      }

      team.roleAssignments.push(assignment)
      return HttpResponse.json(ok(assignmentView(assignment)))
    }),

    http.post('*/api/v1/admin/teams/:teamId/roles/revoke', async ({ request, params }) => {
      await delay(delayFor(scenario()))
      const team = findTeam(String(params.teamId ?? ''))
      if (team === undefined) return notFound()

      const body = await readBody(request)
      const role = String(body.role ?? '').trim()
      const host = String(body.host ?? '').trim()
      const containerId = String(body.containerId ?? '').trim()

      const index = team.roleAssignments.findIndex(
        (assignment) =>
          assignment.role === role &&
          (assignment.hostName ?? '') === host &&
          (assignment.containerId ?? '') === containerId
      )

      if (index < 0) {
        return HttpResponse.json(fail(ErrorCode.TeamRoleAssignmentNotFound), {
          status: httpStatusOf(ErrorCode.TeamRoleAssignmentNotFound)
        })
      }

      team.roleAssignments.splice(index, 1)
      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/admin/teams/:teamId/sync', async ({ request, params }) => {
      await delay(delayFor(scenario()))
      const team = findTeam(String(params.teamId ?? ''))
      if (team === undefined) return notFound()

      if (team.roleAssignments.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '该团队还没有任何角色授权，无需同步'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const body = await readBody(request)
      const requested = Array.isArray(body.userIds) ? body.userIds.map(String) : []
      const targets =
        requested.length > 0
          ? team.memberIds.filter((id) => requested.includes(id))
          : [...team.memberIds]

      let assigned = 0
      let skipped = 0

      for (const userId of targets) {
        for (const assignment of team.roleAssignments) {
          const key = `${userId}|${assignment.roleId}|${assignment.hostId ?? ''}|${assignment.containerId ?? ''}`
          if (syncedAssignments.has(key)) {
            skipped += 1
          } else {
            syncedAssignments.add(key)
            assigned += 1
          }
        }
      }

      const result: TeamSyncResult = { assigned, skipped, members: targets.length }

      return HttpResponse.json(ok(result))
    })
  ]
}
