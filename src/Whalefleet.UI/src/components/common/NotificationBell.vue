<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  subscribeContainerDied,
  subscribeNotification,
  subscribeStackTaskFailed
} from '@/realtime/notification'
import {
  useNotificationStore,
  describe,
  describeContainerDied,
  describeNotification,
  describeStackTaskFailed,
  type NotificationItem
} from '@/stores/notifications'
import { usePreferencesStore } from '@/stores/preferences'
import type {
  ContainerDiedEvent,
  NotificationDto,
  NotificationPayload,
  StackTaskFailureEvent
} from '@/types/api'

/**
 * 顶栏铃铛：容器异常退出通知（0.5.0）与编排任务失败通知（0.10.0）。
 *
 * ## 为什么连的是**全局**通知 Hub
 * 容器异常退出是「平台级事件」——用户在任何页面都应当知道
 * 「某台机器上的某个容器挂了」，而不是必须先进入那台主机才能知道。
 * 用带 `hostId` 的路径会强制用户先选主机，而出事时他未必知道是哪台。
 *
 * 编排任务失败同理：失败可能发生在用户已经离开页面之后
 * （webhook 触发的 CI 部署），那时候他未必正站在那台主机的页面上。
 *
 * ## 可见性
 * 服务端按连接判「该用户对这台主机有没有 `host.read`」，无权的一律不推。
 * 因此前端**不需要**自己过滤主机。
 *
 * ## 连接是共享的
 * 容器列表页也要收同一批事件，两处各建一条连接就是两条长连接。
 * `realtime/notification.ts` 把它们复用到一条上。
 */
const { t } = useI18n()
const router = useRouter()
const notifications = useNotificationStore()
const preferences = usePreferencesStore()

const open = ref(false)

/** 铃铛下拉里的一条条目（落库历史或旧内存态事件）。 */
type BellEntry =
  | { kind: 'history'; key: string; text: string; item: NotificationDto }
  | { kind: 'legacy'; key: string; text: string; item: NotificationItem }

/** 通知条目（含渲染好的文案）。 */
const entries = computed<BellEntry[]>(() => {
  const history = notifications.history.map((item) => {
    const description = describeNotification(item)

    return {
      kind: 'history' as const,
      key: `history:${item.id}`,
      text: t(description.key, description.params),
      item
    }
  })

  const legacy = notifications.items.map((item) => {
    const description = describe(item)

    return {
      kind: 'legacy' as const,
      key: item.key,
      text: t(description.key, description.params),
      item
    }
  })

  return [...history, ...legacy]
})

/** 退订函数。 */
let unsubscribe: (() => void) | null = null
let unsubscribeStack: (() => void) | null = null
let unsubscribeNotification: (() => void) | null = null

/**
 * 处理容器退出事件。
 *
 * @param event 事件。
 */
function onContainerDied(event: ContainerDiedEvent): void {
  notifications.pushContainerDied(event)

  const description = describeContainerDied(event)
  ElMessage.warning(t(description.key, description.params))
}

/**
 * 处理编排任务失败事件。
 *
 * @param event 事件。
 */
function onStackTaskFailed(event: StackTaskFailureEvent): void {
  notifications.pushStackTaskFailed(event)

  const description = describeStackTaskFailed(event)
  ElMessage.error(t(description.key, description.params))
}

/**
 * 处理落库站内通知（0.11.0）。
 *
 * @param event 通知载荷。
 */
function onNotification(event: NotificationPayload): void {
  notifications.pushPayload(event)

  const item = notifications.history.find((entry) => entry.id === event.id)
  const description = item
    ? describeNotification(item)
    : { key: event.messageKey, params: {} as Record<string, string | number> }

  const text = t(description.key, description.params)

  if (event.severity === 'critical') {
    ElMessage.error(text)
  } else if (event.severity === 'warning') {
    ElMessage.warning(text)
  } else {
    ElMessage.info(text)
  }
}

/**
 * 点击一条通知：跳到对应资源的详情。
 *
 * @param item 通知条目。
 */
function openItem(item: NotificationItem): void {
  open.value = false

  if (item.kind === 'container-died') {
    router.push({
      name: 'host-container-inspect',
      params: { hostId: item.event.hostId, id: item.event.containerId }
    })
    return
  }

  router.push({
    name: 'host-stack-detail',
    // ⚠️ 传**原始**名字：自己 encodeURIComponent 会被 vue-router 再编码一次
    params: { hostId: item.event.hostName, name: item.event.stackName }
  })
}

/** 点击一条落库通知：打开通知中心。 */
function openHistory(): void {
  open.value = false
  void router.push({ name: 'notifications' })
}

/**
 * 点击一条条目。
 *
 * @param entry 条目。
 */
function onEntryClick(entry: BellEntry): void {
  if (entry.kind === 'history') {
    openHistory()
    return
  }

  openItem(entry.item)
}

/**
 * 条目的副标题（主机 / 容器上下文）。
 *
 * @param entry 条目。
 * @returns 副标题。
 */
function subtitleOf(entry: BellEntry): string {
  if (entry.kind === 'history') {
    return entry.item.hostName || 'Whalefleet'
  }

  if (entry.item.kind === 'container-died') {
    return `${entry.item.event.hostId} · ${entry.item.event.containerId.slice(0, 12)}`
  }

  return `${entry.item.event.hostName} · ${entry.item.event.stackName}`
}

/** 订阅（或退订）通知事件。 */
function sync(): void {
  unsubscribe?.()
  unsubscribe = null
  unsubscribeStack?.()
  unsubscribeStack = null
  unsubscribeNotification?.()
  unsubscribeNotification = null

  if (preferences.realtimeNotifications) {
    unsubscribe = subscribeContainerDied(onContainerDied)
    unsubscribeStack = subscribeStackTaskFailed(onStackTaskFailed)
    unsubscribeNotification = subscribeNotification(onNotification)
  }
}

watch(() => preferences.realtimeNotifications, sync)

// 打开下拉即把落库通知标记为已读（与 0.5.0「打开即清零」一致，但状态现在会落库）
watch(open, (value) => {
  if (value) {
    void notifications.markAllReadPersisted()
  }
})

onMounted(() => {
  void notifications.fetchRecent()
  sync()
})

onUnmounted(() => {
  unsubscribe?.()
  unsubscribe = null
  unsubscribeStack?.()
  unsubscribeStack = null
  unsubscribeNotification?.()
  unsubscribeNotification = null
})
</script>

<template>
  <el-dropdown v-model:visible="open" trigger="click" placement="bottom-end">
    <button
      type="button"
      class="relative flex items-center justify-center !w-8 !h-8 rounded-md text-content hover:bg-surface-hover transition-colors"
      :aria-label="t('notification.title')"
      @click="open = !open"
    >
      <span class="i-carbon-notification text-xl leading-none" />

      <!-- 红点：无障碍上用 sr-only 文本兜底，不能只靠颜色传达状态。 -->
      <span
        v-if="notifications.hasUnread"
        class="absolute -top-0.5 -right-0.5 h-2 w-2 rounded-full bg-danger ring-2 ring-surface-card"
      />
      <span v-if="notifications.hasUnread" class="sr-only">
        {{ t('notification.unread', { count: notifications.unreadTotal }) }}
      </span>
    </button>

    <template #dropdown>
      <el-dropdown-menu class="min-w-[20rem]">
        <div class="px-3 py-2 text-xs font-medium text-content-muted">
          {{ t('notification.title') }}
        </div>

        <el-dropdown-item v-if="entries.length === 0" disabled>
          <span class="text-xs text-content-faint">{{ t('notification.empty') }}</span>
        </el-dropdown-item>

        <el-dropdown-item
          v-for="entry in entries"
          :key="entry.key"
          @click="onEntryClick(entry)"
        >
          <div class="flex flex-col gap-0.5 py-1">
            <span class="text-xs text-content">{{ entry.text }}</span>
            <span class="text-[10px] text-content-faint">
              <!--
                副标题按类型给不同的上下文：
                容器死亡给「主机 + 容器 ID」，编排失败给「主机 + 项目名」——
                后者给容器 ID 是没有意义的（编排任务不针对某个容器）。
              -->
              {{ subtitleOf(entry) }}
            </span>
          </div>
        </el-dropdown-item>

        <el-dropdown-item divided @click="openHistory">
          <span class="text-xs text-brand">{{ t('notification.viewAll') }}</span>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>
