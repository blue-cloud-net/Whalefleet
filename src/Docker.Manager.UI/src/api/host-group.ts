import { request } from '@/api'
import type { HostGroupSummary } from '@/types/api'

/**
 * 主机分组端点。
 *
 * ## 两组列表端点的分工
 * - `POST /api/v1/host-groups/list`：**所有已登录用户**都能读。总览页按分组聚合展示，
 *   而总览页对所有角色可见——若这里也要求 admin，非管理员的首页会整块报错。
 * - `POST /api/v1/admin/host-groups/list`：管理页用，与上者**同形状**。
 *
 * 两者刻意分开：管理端点带写能力，读端点只读。混用会让权限边界变得说不清。
 */

/** 分组列表（所有已登录用户）。 */
export function listHostGroups(): Promise<HostGroupSummary[]> {
  return request<HostGroupSummary[]>({ url: '/api/v1/host-groups/list', method: 'post' })
}

/** 分组列表（admin 管理页）。 */
export function listAdminHostGroups(): Promise<HostGroupSummary[]> {
  return request<HostGroupSummary[]>({ url: '/api/v1/admin/host-groups/list', method: 'post' })
}

/**
 * 新建分组。
 *
 * 失败形态：`1000`/400（名称非法）、`1304`/400（名称已被占用）。
 */
export function createHostGroup(payload: {
  name: string
  description?: string
  sortOrder?: number
}): Promise<HostGroupSummary> {
  return request<HostGroupSummary>({
    url: '/api/v1/admin/host-groups/create',
    method: 'post',
    data: payload
  })
}

/**
 * 更新分组。
 *
 * **未提供的字段表示不改**；`description` 传空串表示清空
 * （凭据那边需要额外的 clearCredentials 开关，这里一个空串就够，理由见 api-spec）。
 */
export function updateHostGroup(
  groupId: string,
  payload: { name?: string; description?: string; sortOrder?: number }
): Promise<HostGroupSummary> {
  return request<HostGroupSummary>({
    url: `/api/v1/admin/host-groups/${encodeURIComponent(groupId)}/update`,
    method: 'post',
    data: payload
  })
}

/**
 * 删除分组。
 *
 * **不删主机**：组内主机回到「未分组」，返回值是被移出分组的主机数——
 * 界面要据此提示「已把 N 台主机移出该分组」，否则管理员会以为主机也没了。
 */
export function deleteHostGroup(groupId: string): Promise<number> {
  return request<number>({
    url: `/api/v1/admin/host-groups/${encodeURIComponent(groupId)}/delete`,
    method: 'post'
  })
}

/**
 * 调整某台主机的分组归属（`groupId` 传 `null` 即移出分组）。
 *
 * 单设端点而不是复用 `hosts/{hostId}/update`：那边的「未提供表示不改」语义
 * 分不清「没给 groupId」与「给了 null」，于是「移出分组」就没有表达方式。
 */
export function assignHostGroup(hostId: string, groupId: string | null): Promise<void> {
  return request<void>({
    url: `/api/v1/admin/hosts/${encodeURIComponent(hostId)}/group`,
    method: 'post',
    data: { groupId }
  })
}
