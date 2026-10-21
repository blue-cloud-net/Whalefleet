import { defineStore } from 'pinia'
import type { ContainerDiedEvent } from '@/types/api'

/**
 * 通知 store：顶栏铃铛里的容器异常退出通知。
 *
 * ## 为什么是内存态而不是落库
 * 0.5.0 的通知只回答「**此刻**有没有出事」——错过的事件没有补看的必要
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
    /** 通知列表（最新的在前）。 */
    items: [] as ContainerDiedEvent[],
    /** 未读条数。 */
    unread: 0
  }),
  getters: {
    /** 是否有未读通知。 */
    hasUnread: (state) => state.unread > 0
  },
  actions: {
    /**
     * 推入一条通知。
     *
     * @param event 容器异常退出事件。
     */
    push(event: ContainerDiedEvent): void {
      this.items.unshift(event)

      if (this.items.length > MAX_ITEMS) {
        // 超出上限：丢最旧的（列表尾部）。
        this.items.length = MAX_ITEMS
      }

      this.unread += 1
    },

    /** 全部标记为已读。 */
    markAllRead(): void {
      this.unread = 0
    },

    /** 清空全部通知。 */
    clear(): void {
      this.items = []
      this.unread = 0
    }
  }
})

/**
 * 把容器异常退出事件翻译成一句人话的**描述**（i18n key + 参数）。
 *
 * @param event 事件。
 * @returns 描述。
 */
export function describe(event: ContainerDiedEvent): NotificationDescription {
  const name = event.containerName || event.containerId.slice(0, 12)

  if (event.outOfMemory) {
    return { key: 'notification.oom', params: { name, code: event.exitCode } }
  }

  if (event.exitCode === 0) {
    return { key: 'notification.exited', params: { name } }
  }

  return { key: 'notification.died', params: { name, code: event.exitCode } }
}
