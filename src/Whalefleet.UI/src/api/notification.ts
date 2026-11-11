import { request } from '@/api'
import type { NotificationListResult, NotificationQuery } from '@/types/api'

/**
 * 站内通知端点（0.11.0）。
 *
 * 可见性由服务端按主机权限过滤，因此前端**不需要**再按主机筛一遍。
 */

/**
 * 分页列出当前用户可见的通知。
 *
 * @param query 过滤条件。
 * @returns 分页结果（含未读数）。
 */
export function listNotifications(query: NotificationQuery = {}): Promise<NotificationListResult> {
  return request<NotificationListResult>({
    url: '/api/v1/notifications/list',
    method: 'post',
    data: query
  })
}

/**
 * 标记通知已读。
 *
 * @param ids 通知主键；不传且 all=true 表示全部可见。
 * @param all 是否全部标记。
 * @returns 影响行数。
 */
export function markNotificationsRead(ids?: string[], all = false): Promise<number> {
  return request<number>({
    url: '/api/v1/notifications/read',
    method: 'post',
    data: { ids: ids ?? null, all }
  })
}

/**
 * 删除（对本人隐藏）通知。
 *
 * @param ids 通知主键；不传且 all=true 表示全部可见。
 * @param all 是否全部删除。
 * @returns 影响行数。
 */
export function removeNotifications(ids?: string[], all = false): Promise<number> {
  return request<number>({
    url: '/api/v1/notifications/remove',
    method: 'post',
    data: { ids: ids ?? null, all }
  })
}
