import type { AlertRule, NotificationChannel, NotificationDto } from '@/types/api'

/** 告警规则夹具（与迁移里的默认规则一致）。 */
export const MOCK_ALERT_RULES: AlertRule[] = [
  {
    id: 'rule-host-offline',
    hostId: null,
    hostName: null,
    kind: 'host-offline',
    enabled: true,
    infoThreshold: null,
    warningThreshold: null,
    criticalThreshold: null,
    severity: 'critical',
    consecutiveSamples: 2,
    windowSeconds: null,
    cooldownSeconds: 300,
    channelIds: ['channel-webhook'],
    createdAt: '2026-01-01T00:00:00Z'
  },
  {
    id: 'rule-container-cpu',
    hostId: null,
    hostName: null,
    kind: 'container-cpu',
    enabled: false,
    infoThreshold: 70,
    warningThreshold: 85,
    criticalThreshold: 95,
    severity: 'warning',
    consecutiveSamples: 3,
    windowSeconds: null,
    cooldownSeconds: 300,
    channelIds: [],
    createdAt: '2026-01-01T00:00:00Z'
  },
  {
    id: 'rule-container-memory',
    hostId: null,
    hostName: null,
    kind: 'container-memory',
    enabled: false,
    infoThreshold: 70,
    warningThreshold: 85,
    criticalThreshold: 95,
    severity: 'warning',
    consecutiveSamples: 3,
    windowSeconds: null,
    cooldownSeconds: 300,
    channelIds: [],
    createdAt: '2026-01-01T00:00:00Z'
  },
  {
    id: 'rule-login-surge',
    hostId: null,
    hostName: null,
    kind: 'login-failure-surge',
    enabled: false,
    infoThreshold: 5,
    warningThreshold: 10,
    criticalThreshold: 20,
    severity: 'warning',
    consecutiveSamples: 1,
    windowSeconds: 300,
    cooldownSeconds: 300,
    channelIds: [],
    createdAt: '2026-01-01T00:00:00Z'
  }
]

/** 通知渠道夹具（响应里不含令牌）。 */
export const MOCK_NOTIFICATION_CHANNELS: NotificationChannel[] = [
  {
    id: 'channel-webhook',
    name: '值班 Webhook',
    kind: 'webhook',
    enabled: true,
    webhookUrl: 'https://hooks.example.com/whalefleet',
    hasWebhookToken: true,
    emailRecipients: null
  }
]

/** 站内通知夹具。 */
export const MOCK_NOTIFICATIONS: NotificationDto[] = [
  {
    id: 'n1',
    sourceKind: 'alert-fired',
    severity: 'critical',
    messageKey: 'notification.alert.hostOffline',
    params: { host: 'prod-01' },
    hostId: null,
    hostName: 'prod-01',
    read: false,
    readAt: null,
    occurredAt: '2026-11-10T10:00:00Z'
  },
  {
    id: 'n2',
    sourceKind: 'container-died',
    severity: 'warning',
    messageKey: 'notification.died',
    params: { name: 'web', code: 1 },
    hostId: null,
    hostName: 'local',
    read: false,
    readAt: null,
    occurredAt: '2026-11-10T09:30:00Z'
  },
  {
    id: 'n3',
    sourceKind: 'compose-task-failed',
    severity: 'critical',
    messageKey: 'notification.composeFailed',
    params: { host: 'local', compose: 'shop', message: '拉取镜像失败' },
    hostId: null,
    hostName: 'local',
    read: true,
    readAt: '2026-11-10T09:00:00Z',
    occurredAt: '2026-11-10T08:50:00Z'
  }
]
