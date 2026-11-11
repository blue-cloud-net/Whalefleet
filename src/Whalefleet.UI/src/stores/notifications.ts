import { defineStore } from 'pinia'
import {
  listNotifications,
  markNotificationsRead,
  removeNotifications
} from '@/api/notification'
import type {
  ContainerDiedEvent,
  NotificationDto,
  NotificationPayload,
  ComposeTaskFailureEvent
} from '@/types/api'

/**
 * 通知 store：顶栏铃铛里的容器异常退出通知与编排任务失败通知。
 *
 * ## 为什么是内存态而不是落库
 * 通知只回答「**此刻**有没有出事」——错过的事件没有补看的必要
 * （要看当前状态，去查容器详情即可）。落库会带来「通知表 + 保留策略 + 迁移」
 * 这一整套与本迭代无关的成本，而且完整的通知中心（历史、搜索、标记已读）
 * 排在 0.11.0。
 *
 * ## 为什么上限是 20 条
 * 铃铛下拉里只展示最近的几条；更多留在内存里也没有意义（用户不会翻到第 21 条）。
 * 超出即丢最旧的。
 */

/** 内存中保留的通知条数上限。 */
const MAX_ITEMS = 20

/**
 * 一条通知。
 *
 * 用**判别联合**而不是「一个大接口带一堆可空字段」：
 * 后者会让模板里到处是 `item.containerId ?? item.composeName` 这种分支，
 * 而编译期又拦不住「拿错了字段」。
 */
export type NotificationItem =
  | {
      kind: 'container-died'
      /** 去重用的键 */
      key: string
      occurredAt: string
      event: ContainerDiedEvent
    }
  | {
      kind: 'compose-task-failed'
      /** 去重用的键 */
      key: string
      occurredAt: string
      event: ComposeTaskFailureEvent
    }

/**
 * 通知的展示描述。
 *
 * 只给 **i18n key + 参数**，不给文案——文案要能被翻译，
 * 而且通知文案会出现两处（toast 与铃铛下拉），两处写得不一样时
 * 用户会以为看到的是两件事——而它们本来就是同一件事。
 */
export interface NotificationDescription {
  /** i18n key。 */
  key: string
  /** 插值参数。 */
  params: Record<string, string | number>
}

/**
 * 通知 store。
 */
export const useNotificationStore = defineStore('notifications', {
  state: () => ({
    /** 旧的内存态实时通知（保留给既有 toast / 测试）。 */
    items: [] as NotificationItem[],
    /** 旧的未读条数。 */
    unread: 0,
    /** 落库的通知历史（0.11.0）。 */
    history: [] as NotificationDto[],
    /** 历史总数。 */
    historyTotal: 0,
    /** 历史未读数。 */
    historyUnread: 0,
    /** 历史是否正在加载。 */
    historyLoading: false
  }),
  getters: {
    /** 是否有未读通知（旧内存态 + 落库历史）。 */
    hasUnread: (state) => state.unread + state.historyUnread > 0,
    /** 未读总数。 */
    unreadTotal: (state) => state.unread + state.historyUnread
  },
  actions: {
    /**
     * 推入一条通知。
     *
     * @param item 通知条目。
     */
    push(item: NotificationItem): void {
      // 去重（0.6.0）：同一次死亡可能被多个通道带来（
      // 例如「列表页刷新回调」与「全局通知 Hub」同时到达）。
      // 不去重的话铃铛里会出现两条一模一样的条目，用户会以为容器死了两次。
      const duplicate = this.items.some((existing) => existing.key === item.key)

      if (duplicate) return

      this.items.unshift(item)

      if (this.items.length > MAX_ITEMS) {
        // 超出上限：丢最旧的（列表尾部）。
        this.items.length = MAX_ITEMS
      }

      this.unread += 1
    },

    /**
     * 推入一条「容器异常退出」通知。
     *
     * @param event 容器异常退出事件。
     */
    pushContainerDied(event: ContainerDiedEvent): void {
      this.push({
        kind: 'container-died',
        key: `container:${event.hostId}:${event.containerId}:${event.occurredAt}`,
        occurredAt: event.occurredAt,
        event
      })
    },

    /**
     * 推入一条「编排任务失败」通知。
     *
     * @param event 编排任务失败事件。
     */
    pushComposeProjectTaskFailed(event: ComposeTaskFailureEvent): void {
      this.push({
        kind: 'compose-task-failed',
        key: `compose:${event.hostName}:${event.composeName}:${event.occurredAt}`,
        occurredAt: event.occurredAt,
        event
      })
    },

    /** 全部标记为已读。 */
    markAllRead(): void {
      this.unread = 0
    },

    /** 清空全部通知（仅旧内存态）。 */
    clear(): void {
      this.items = []
      this.unread = 0
    },

    /**
     * 拉取最近的通知历史（铃铛下拉用）。
     */
    async fetchRecent(): Promise<void> {
      this.historyLoading = true
      try {
        const result = await listNotifications({ pageIndex: 0, pageSize: 20 })
        this.history = result.items
        this.historyTotal = result.total
        this.historyUnread = result.unread
      } catch {
        // 没有历史不影响其它功能；保持现有数据。
      } finally {
        this.historyLoading = false
      }
    },

    /**
     * 把实时推送的通知并入历史（去重）。
     *
     * @param payload `OnNotification` 载荷。
     */
    pushPayload(payload: NotificationPayload): void {
      if (this.history.some((item) => item.id === payload.id)) return

      let params: NotificationDto['params'] = {}

      try {
        params = JSON.parse(payload.paramsJson) as NotificationDto['params']
      } catch {
        params = {}
      }

      this.history.unshift({
        id: payload.id,
        sourceKind: payload.sourceKind,
        severity: payload.severity,
        messageKey: payload.messageKey,
        params,
        hostId: payload.hostId,
        hostName: payload.hostName,
        read: false,
        readAt: null,
        occurredAt: payload.occurredAt
      })

      this.history = this.history.slice(0, 20)
      this.historyTotal += 1
      this.historyUnread += 1
    },

    /**
     * 把全部可见通知标记为已读（落库）。
     */
    async markAllReadPersisted(): Promise<void> {
      try {
        await markNotificationsRead(undefined, true)
      } finally {
        this.unread = 0
        this.history = this.history.map((item) => ({
          ...item,
          read: true,
          readAt: item.readAt ?? new Date().toISOString()
        }))
        this.historyUnread = 0
      }
    },

    /**
     * 删除（对本人隐藏）通知。
     *
     * @param ids 通知主键。
     */
    async removePersisted(ids: string[]): Promise<void> {
      await removeNotifications(ids)

      const removed = this.history.filter((item) => ids.includes(item.id))
      const removedUnread = removed.filter((item) => !item.read).length

      this.history = this.history.filter((item) => !ids.includes(item.id))
      this.historyTotal = Math.max(0, this.historyTotal - removed.length)
      this.historyUnread = Math.max(0, this.historyUnread - removedUnread)
    }
  }
})

/**
 * 把容器异常退出事件翻译成一句人话的**描述**（i18n key + 参数）。
 *
 * @param event 事件。
 * @returns 描述。
 */
export function describeContainerDied(event: ContainerDiedEvent): NotificationDescription {
  const name = event.containerName || event.containerId.slice(0, 12)

  if (event.outOfMemory) {
    return { key: 'notification.oom', params: { name, code: event.exitCode } }
  }

  if (event.exitCode === 0) {
    return { key: 'notification.exited', params: { name } }
  }

  return { key: 'notification.died', params: { name, code: event.exitCode } }
}

/**
 * 把编排任务失败事件翻译成一句人话的**描述**（i18n key + 参数）。
 *
 * @param event 事件。
 * @returns 描述。
 */
export function describeComposeTaskFailed(event: ComposeTaskFailureEvent): NotificationDescription {
  return {
    key: 'notification.composeFailed',
    params: { host: event.hostName, compose: event.composeName, message: event.message }
  }
}

/**
 * 把一条通知翻译成一句人话的**描述**（i18n key + 参数）。
 *
 * @param item 通知条目。
 * @returns 描述。
 */
export function describe(item: NotificationItem): NotificationDescription {
  return item.kind === 'container-died'
    ? describeContainerDied(item.event)
    : describeComposeTaskFailed(item.event)
}

/**
 * 把一条落库通知翻译成描述（服务端已给 i18n key + 参数）。
 *
 * @param item 通知。
 * @returns 描述。
 */
export function describeNotification(item: NotificationDto): NotificationDescription {
  return {
    key: item.messageKey,
    // i18n 只接受标量参数；bool / null 在现有告警文案里不存在，
    // 这里做一次窄化避免模板类型检查失败。
    params: item.params as Record<string, string | number>
  }
}
