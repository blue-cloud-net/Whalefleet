import { request } from '@/api'
import type {
  TeamDetail,
  TeamListResult,
  TeamRoleAssignmentSummary,
  TeamSummary,
  TeamSyncResult
} from '@/types/api'

/**
 * 团队管理端点（**全局作用域**，路径不含 `hostId`）。
 *
 * ## 团队只收窄、不扩权
 * 给团队授角色只是设定成员在已授权作用域内的**权限上限**；
 * 成员的**个人**授权仍在用户侧的角色分配里（或由本文件的 `syncTeam` 显式写入）。
 *
 * **加入团队不会自动获得权限**——界面必须把这句话说出来，
 * 否则用户会以为「把人加进团队就有权限了」。
 *
 * 路径与请求 / 响应结构必须与 `docs/api-spec.md` 的「团队与团队权限」一致。
 */

/** 团队列表（分页）。 */
export function listTeams(
  params: { pageIndex?: number; pageSize?: number } = {}
): Promise<TeamListResult> {
  return request<TeamListResult>({
    url: '/api/v1/admin/teams/list',
    method: 'post',
    data: params
  })
}

/** 团队详情（成员 + 角色授权）。 */
export function getTeamDetail(teamId: string): Promise<TeamDetail> {
  return request<TeamDetail>({
    url: `/api/v1/admin/teams/${encodeURIComponent(teamId)}/detail`,
    method: 'post'
  })
}

/**
 * 创建团队。
 *
 * 失败形态：`1000`/400（字段非法）、`1203`/400（团队名已占用）。
 */
export function createTeam(payload: {
  name: string
  displayName?: string
  description?: string
}): Promise<TeamSummary> {
  return request<TeamSummary>({
    url: '/api/v1/admin/teams/create',
    method: 'post',
    data: payload
  })
}

/** 更新团队（可读名称 / 备注）。 */
export function updateTeam(payload: {
  teamId: string
  displayName?: string
  description?: string
}): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/teams/update',
    method: 'post',
    data: payload
  })
}

/**
 * 删除团队（软删除）。
 *
 * ⚠️ **不回收已发放的授权**：团队只是权限上限的持有者，
 * 成员的个人授权是管理员单独授予的，不随团队删除而消失。
 */
export function removeTeam(teamId: string): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/teams/remove',
    method: 'post',
    data: { teamId }
  })
}

/**
 * 添加团队成员。
 *
 * ⚠️ **不会授予任何权限**：加入团队只是进入「权限上限」的适用范围。
 * 要让成员真的拿到权限，需要另外执行「同步到成员」。
 */
export function addTeamMembers(teamId: string, userIds: string[]): Promise<number> {
  return request<number>({
    url: `/api/v1/admin/teams/${encodeURIComponent(teamId)}/members/add`,
    method: 'post',
    data: { userIds }
  })
}

/** 移除团队成员（其个人授权不受影响）。 */
export function removeTeamMembers(teamId: string, userIds: string[]): Promise<number> {
  return request<number>({
    url: `/api/v1/admin/teams/${encodeURIComponent(teamId)}/members/remove`,
    method: 'post',
    data: { userIds }
  })
}

/**
 * 给团队授予角色（**权限上限**，幂等）。
 *
 * `host` 省略即全局；`containerId` 必须同时带 `host`。
 */
export function assignTeamRole(
  teamId: string,
  payload: { role?: string; roleId?: string; host?: string; containerId?: string }
): Promise<TeamRoleAssignmentSummary> {
  return request<TeamRoleAssignmentSummary>({
    url: `/api/v1/admin/teams/${encodeURIComponent(teamId)}/roles/assign`,
    method: 'post',
    data: payload
  })
}

/** 撤销团队的角色授权（软删除）。 */
export function revokeTeamRole(
  teamId: string,
  payload: { role?: string; roleId?: string; host?: string; containerId?: string }
): Promise<void> {
  return request<void>({
    url: `/api/v1/admin/teams/${encodeURIComponent(teamId)}/roles/revoke`,
    method: 'post',
    data: payload
  })
}

/**
 * 把团队的角色授权**同步**到成员的个人授权（显式动作，幂等）。
 *
 * `userIds` 省略 = 同步给全部成员。
 */
export function syncTeam(teamId: string, userIds?: string[]): Promise<TeamSyncResult> {
  return request<TeamSyncResult>({
    url: `/api/v1/admin/teams/${encodeURIComponent(teamId)}/sync`,
    method: 'post',
    data: { userIds }
  })
}
