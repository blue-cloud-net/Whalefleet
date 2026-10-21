import { request } from '@/api'
import type { HostTag } from '@/types/api'

/**
 * 主机标签端点（0.6.0）。
 *
 * ## 路径约定（见 docs/api-spec.md）
 *
 * - `POST /api/v1/host-tags/list` —— 任意已登录用户可读（过滤栏要用）
 * - `POST /api/v1/admin/host-tags/*` —— 标签自身的管理（admin）
 * - `POST /api/v1/admin/hosts/tags/*` —— 批量打标 / 清标（admin，主语是主机）
 *
 * ## 为什么打标请求体里是主机名而不是主键
 *
 * 与 `hosts/{hostId}` 路径同口径：前端手里只有主机名，`hosts.id`（Guid）只在服务端内部流转。
 */

/** 列出全部标签（供过滤与打标用）。 */
export function listHostTags(): Promise<HostTag[]> {
  return request<HostTag[]>({ url: '/api/v1/host-tags/list', method: 'post' })
}

/** 新建标签。失败形态：`1000`/400（名字或颜色非法）、`1307`/400（名称被占用）。 */
export function createHostTag(payload: {
  name: string
  description?: string | null
  color?: string | null
}): Promise<HostTag> {
  return request<HostTag>({
    url: '/api/v1/admin/host-tags/create',
    method: 'post',
    data: payload
  })
}

/**
 * 更新标签。
 *
 * **未提供的字段表示不改**；`description` / `color` 传空串表示清空。
 */
export function updateHostTag(
  tagId: string,
  payload: { name?: string; description?: string; color?: string }
): Promise<HostTag> {
  return request<HostTag>({
    url: `/api/v1/admin/host-tags/${encodeURIComponent(tagId)}/update`,
    method: 'post',
    data: payload
  })
}

/** 删除标签（关联一并清除，**不删主机**）；返回被解除关联的主机数。 */
export function deleteHostTag(tagId: string): Promise<number> {
  return request<number>({
    url: `/api/v1/admin/host-tags/${encodeURIComponent(tagId)}/delete`,
    method: 'post'
  })
}

/**
 * 批量打标（幂等）。
 *
 * `hostIds` 是**主机名**列表，不是主键——与 `hosts/{hostId}` 同口径。
 */
export function assignHostTags(payload: {
  hostIds: string[]
  tagIds: string[]
}): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/hosts/tags/assign',
    method: 'post',
    data: payload
  })
}

/** 批量清标（幂等）。 */
export function revokeHostTags(payload: {
  hostIds: string[]
  tagIds: string[]
}): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/hosts/tags/revoke',
    method: 'post',
    data: payload
  })
}
