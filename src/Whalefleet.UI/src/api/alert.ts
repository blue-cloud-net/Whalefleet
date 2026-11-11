import { request } from '@/api'
import type {
  AlertRule,
  NotificationChannel,
  SaveAlertRuleRequest,
  SaveNotificationChannelRequest
} from '@/types/api'

/**
 * 告警规则与通知渠道端点（0.11.0）。
 *
 * 规则/渠道都是**全局配置**，由 `alert.read` / `alert.write` 把守。
 * 请求体里的主机用主机名 `host` 表达，不带主机主键。
 */

/**
 * 列出告警规则（全局 + 指定主机）。
 *
 * @param host 主机名；省略表示只看全局规则。
 * @returns 规则列表。
 */
export function listAlertRules(host?: string | null): Promise<AlertRule[]> {
  const query = host ? `?host=${encodeURIComponent(host)}` : ''

  return request<AlertRule[]>({
    url: `/api/v1/admin/alert-rules/list${query}`,
    method: 'post'
  })
}

/**
 * 新建告警规则。
 *
 * @param payload 请求。
 * @returns 新建的规则。
 */
export function createAlertRule(payload: SaveAlertRuleRequest): Promise<AlertRule> {
  return request<AlertRule>({
    url: '/api/v1/admin/alert-rules/create',
    method: 'post',
    data: payload
  })
}

/**
 * 更新告警规则。
 *
 * @param payload 请求（必须带 id）。
 * @returns 更新后的规则。
 */
export function updateAlertRule(payload: SaveAlertRuleRequest): Promise<AlertRule> {
  return request<AlertRule>({
    url: '/api/v1/admin/alert-rules/update',
    method: 'post',
    data: payload
  })
}

/**
 * 启用 / 停用告警规则。
 *
 * @param id 规则主键。
 * @param enabled 是否启用。
 */
export function setAlertRuleEnabled(id: string, enabled: boolean): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/alert-rules/set-enabled',
    method: 'post',
    data: { id, enabled }
  })
}

/**
 * 删除告警规则。
 *
 * @param id 规则主键。
 */
export function removeAlertRule(id: string): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/alert-rules/remove',
    method: 'post',
    data: { id }
  })
}

/**
 * 列出通知渠道。
 *
 * @returns 渠道列表（不含令牌 / 密码）。
 */
export function listNotificationChannels(): Promise<NotificationChannel[]> {
  return request<NotificationChannel[]>({
    url: '/api/v1/admin/notification-channels/list',
    method: 'post'
  })
}

/**
 * 新建通知渠道。
 *
 * @param payload 请求。
 * @returns 新建的渠道。
 */
export function createNotificationChannel(
  payload: SaveNotificationChannelRequest
): Promise<NotificationChannel> {
  return request<NotificationChannel>({
    url: '/api/v1/admin/notification-channels/create',
    method: 'post',
    data: payload
  })
}

/**
 * 更新通知渠道。
 *
 * @param payload 请求（必须带 id）。
 * @returns 更新后的渠道。
 */
export function updateNotificationChannel(
  payload: SaveNotificationChannelRequest
): Promise<NotificationChannel> {
  return request<NotificationChannel>({
    url: '/api/v1/admin/notification-channels/update',
    method: 'post',
    data: payload
  })
}

/**
 * 删除通知渠道。
 *
 * @param id 渠道主键。
 */
export function removeNotificationChannel(id: string): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/notification-channels/remove',
    method: 'post',
    data: { id }
  })
}

/**
 * 发送渠道测试消息。
 *
 * @param id 渠道主键。
 */
export function testNotificationChannel(id: string): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/notification-channels/test',
    method: 'post',
    data: { id }
  })
}
