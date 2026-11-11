import { http, HttpResponse } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import {
  MOCK_ALERT_RULES,
  MOCK_NOTIFICATION_CHANNELS,
  MOCK_NOTIFICATIONS
} from '../data/alerts'
import type {
  AlertRule,
  NotificationChannel,
  NotificationDto,
  SaveAlertRuleRequest,
  SaveNotificationChannelRequest
} from '@/types/api'

/** 会话内可变状态（不原地改夹具，测试可重置）。 */
let rules: AlertRule[] = [...MOCK_ALERT_RULES]
let channels: NotificationChannel[] = [...MOCK_NOTIFICATION_CHANNELS]
let notifications: NotificationDto[] = [...MOCK_NOTIFICATIONS]

/** 清空会话内改动（测试隔离）。 */
export function resetAlertMockState(): void {
  rules = [...MOCK_ALERT_RULES]
  channels = [...MOCK_NOTIFICATION_CHANNELS]
  notifications = [...MOCK_NOTIFICATIONS]
}

/** 读取请求体。 */
async function readBody<T>(request: Request): Promise<T> {
  try {
    return (await request.json()) as T
  } catch {
    return {} as T
  }
}

/**
 * 告警规则 / 通知渠道 / 通知中心的 mock。
 *
 * 复刻要点：
 * - 规则与渠道的写操作会真实反映到列表（否则「保存成功但列表没变」）。
 * - 通知列表按用户可见性过滤——这里夹具都无 hostId（全局），因此全部可见。
 * - 删除渠道不级联删除规则里的 channelIds，界面仍能看出「绑了但渠道没了」。
 */
export function alertHandlers() {
  return [
    http.post('/api/v1/admin/alert-rules/list', () => HttpResponse.json(ok(rules))),

    http.post('/api/v1/admin/alert-rules/create', async ({ request }) => {
      const body = await readBody<SaveAlertRuleRequest>(request)
      const rule: AlertRule = {
        id: `rule-${Date.now()}`,
        hostId: null,
        hostName: body.host ?? null,
        kind: body.kind,
        enabled: body.enabled,
        infoThreshold: body.infoThreshold,
        warningThreshold: body.warningThreshold,
        criticalThreshold: body.criticalThreshold,
        severity: body.severity,
        consecutiveSamples: body.consecutiveSamples,
        windowSeconds: body.windowSeconds,
        cooldownSeconds: body.cooldownSeconds,
        channelIds: body.channelIds ?? [],
        createdAt: new Date().toISOString()
      }

      if (
        body.infoThreshold !== null &&
        body.warningThreshold !== null &&
        body.criticalThreshold !== null &&
        !(body.infoThreshold < body.warningThreshold && body.warningThreshold < body.criticalThreshold)
      ) {
        return HttpResponse.json(
          fail(ErrorCode.AlertRuleInvalid, '阈值必须严格递增'),
          { status: httpStatusOf(ErrorCode.AlertRuleInvalid) }
        )
      }

      rules = [...rules, rule]
      return HttpResponse.json(ok(rule))
    }),

    http.post('/api/v1/admin/alert-rules/update', async ({ request }) => {
      const body = await readBody<SaveAlertRuleRequest>(request)
      const index = rules.findIndex((rule) => rule.id === body.id)

      if (index < 0) {
        return HttpResponse.json(
          fail(ErrorCode.AlertRuleNotFound, '告警规则不存在'),
          { status: httpStatusOf(ErrorCode.AlertRuleNotFound) }
        )
      }

      const updated: AlertRule = {
        ...rules[index]!,
        hostName: body.host ?? null,
        kind: body.kind,
        enabled: body.enabled,
        infoThreshold: body.infoThreshold,
        warningThreshold: body.warningThreshold,
        criticalThreshold: body.criticalThreshold,
        severity: body.severity,
        consecutiveSamples: body.consecutiveSamples,
        windowSeconds: body.windowSeconds,
        cooldownSeconds: body.cooldownSeconds,
        channelIds: body.channelIds ?? []
      }

      rules = rules.map((rule) => (rule.id === updated.id ? updated : rule))
      return HttpResponse.json(ok(updated))
    }),

    http.post('/api/v1/admin/alert-rules/set-enabled', async ({ request }) => {
      const body = await readBody<{ id: string; enabled: boolean }>(request)
      rules = rules.map((rule) =>
        rule.id === body.id ? { ...rule, enabled: body.enabled } : rule
      )
      return HttpResponse.json(ok(null))
    }),

    http.post('/api/v1/admin/alert-rules/remove', async ({ request }) => {
      const body = await readBody<{ id: string }>(request)
      rules = rules.filter((rule) => rule.id !== body.id)
      return HttpResponse.json(ok(null))
    }),

    http.post('/api/v1/admin/notification-channels/list', () => HttpResponse.json(ok(channels))),

    http.post('/api/v1/admin/notification-channels/create', async ({ request }) => {
      const body = await readBody<SaveNotificationChannelRequest>(request)
      const channel: NotificationChannel = {
        id: `channel-${Date.now()}`,
        name: body.name,
        kind: body.kind,
        enabled: body.enabled,
        webhookUrl: body.kind === 'webhook' ? (body.webhookUrl ?? null) : null,
        hasWebhookToken: Boolean(body.webhookBearerToken),
        emailRecipients: body.kind === 'email' ? (body.emailRecipients ?? []) : null
      }
      channels = [...channels, channel]
      return HttpResponse.json(ok(channel))
    }),

    http.post('/api/v1/admin/notification-channels/update', async ({ request }) => {
      const body = await readBody<SaveNotificationChannelRequest>(request)
      const index = channels.findIndex((channel) => channel.id === body.id)

      if (index < 0) {
        return HttpResponse.json(
          fail(ErrorCode.NotificationChannelNotFound, '通知渠道不存在'),
          { status: httpStatusOf(ErrorCode.NotificationChannelNotFound) }
        )
      }

      const updated: NotificationChannel = {
        ...channels[index]!,
        name: body.name,
        kind: body.kind,
        enabled: body.enabled,
        webhookUrl: body.kind === 'webhook' ? (body.webhookUrl ?? null) : null,
        hasWebhookToken: Boolean(body.webhookBearerToken),
        emailRecipients: body.kind === 'email' ? (body.emailRecipients ?? []) : null
      }

      channels = channels.map((channel) => (channel.id === updated.id ? updated : channel))
      return HttpResponse.json(ok(updated))
    }),

    http.post('/api/v1/admin/notification-channels/remove', async ({ request }) => {
      const body = await readBody<{ id: string }>(request)
      channels = channels.filter((channel) => channel.id !== body.id)
      return HttpResponse.json(ok(null))
    }),

    http.post('/api/v1/admin/notification-channels/test', async ({ request }) => {
      const body = await readBody<{ id: string }>(request)
      const channel = channels.find((item) => item.id === body.id)

      if (!channel) {
        return HttpResponse.json(
          fail(ErrorCode.NotificationChannelNotFound, '通知渠道不存在'),
          { status: httpStatusOf(ErrorCode.NotificationChannelNotFound) }
        )
      }

      // 地址含 unreachable 时模拟失败
      if (channel.webhookUrl?.includes('unreachable')) {
        return HttpResponse.json(
          fail(ErrorCode.NotificationChannelTestFailed, '连接超时'),
          { status: httpStatusOf(ErrorCode.NotificationChannelTestFailed) }
        )
      }

      return HttpResponse.json(ok(null))
    }),

    http.post('/api/v1/notifications/list', async ({ request }) => {
      const body = await readBody<{
        unreadOnly?: boolean | null
        severity?: string | null
        sourceKind?: string | null
        search?: string | null
        pageIndex?: number
        pageSize?: number
      }>(request)

      const filtered = notifications.filter((item) => {
        if (body.unreadOnly && item.read) return false
        if (body.severity && item.severity !== body.severity) return false
        if (body.sourceKind && item.sourceKind !== body.sourceKind) return false
        if (body.search) {
          const keyword = body.search.toLowerCase()
          if (
            !item.messageKey.toLowerCase().includes(keyword) &&
            !(item.hostName ?? '').toLowerCase().includes(keyword)
          ) {
            return false
          }
        }
        return true
      })

      const pageSize = Math.min(Math.max(body.pageSize ?? 20, 1), 100)
      const pageIndex = Math.max(body.pageIndex ?? 0, 0)
      const items = filtered.slice(pageIndex * pageSize, pageIndex * pageSize + pageSize)

      return HttpResponse.json(
        ok({
          items,
          total: filtered.length,
          unread: notifications.filter((item) => !item.read).length,
          pageIndex,
          pageSize
        })
      )
    }),

    http.post('/api/v1/notifications/read', async ({ request }) => {
      const body = await readBody<{ ids: string[] | null; all: boolean }>(request)
      const now = new Date().toISOString()

      notifications = notifications.map((item) =>
        body.all || (body.ids ?? []).includes(item.id)
          ? { ...item, read: true, readAt: item.readAt ?? now }
          : item
      )

      return HttpResponse.json(ok(lettersRead(notifications)))
    }),

    http.post('/api/v1/notifications/remove', async ({ request }) => {
      const body = await readBody<{ ids: string[] | null; all: boolean }>(request)
      const removed = body.all
        ? notifications.length
        : notifications.filter((item) => (body.ids ?? []).includes(item.id)).length

      notifications = body.all
        ? []
        : notifications.filter((item) => !(body.ids ?? []).includes(item.id))

      return HttpResponse.json(ok(removed))
    })
  ]
}

/** 已读条数（响应里的影响行数）。 */
function lettersRead(items: NotificationDto[]): number {
  return items.filter((item) => item.read).length
}
