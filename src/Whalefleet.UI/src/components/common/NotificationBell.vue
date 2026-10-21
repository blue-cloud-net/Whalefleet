<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { subscribeContainerDied } from '@/realtime/notification'
import { useNotificationStore, describe } from '@/stores/notifications'
import { usePreferencesStore } from '@/stores/preferences'
import type { ContainerDiedEvent } from '@/types/api'

/**
 * 顶栏铃铛：容器异常退出通知（0.5.0）。
 *
 * ## 为什么连的是**全局**通知 Hub
 * 容器异常退出是「平台级事件」——用户在任何页面都应当知道
 * 「某台机器上的某个容器挂了」，而不是必须先进入那台主机才能知道。
 * 用带 `hostId` 的路径会强制用户先选主机，而出事时他未必知道是哪台。
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

/** 通知条目（含渲染好的文案）。 */
const entries = computed(() =>
  notifications.items.map((item) => {
    const description = describe(item)
    return { event: item, text: t(description.key, description.params) }
  })
)

/** 退订函数。 */
let unsubscribe: (() => void) | null = null

/**
 * 处理容器退出事件。
 *
 * @param event 事件。
 */
function onContainerDied(event: ContainerDiedEvent): void {
  notifications.push(event)

  const description = describe(event)
  ElMessage.warning(t(description.key, description.params))
}

/** 点击一条通知：跳到那个容器的详情页。 */
function openContainer(event: ContainerDiedEvent): void {
  open.value = false

  router.push({
    name: 'host-container-inspect',
    params: { hostId: event.hostId, id: event.containerId }
  })
}

/** 订阅（或退订）通知事件。 */
function sync(): void {
  unsubscribe?.()
  unsubscribe = null

  if (preferences.realtimeNotifications) {
    unsubscribe = subscribeContainerDied(onContainerDied)
  }
}

watch(() => preferences.realtimeNotifications, sync)

onMounted(sync)

onUnmounted(() => {
  unsubscribe?.()
  unsubscribe = null
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
        {{ t('notification.unread', { count: notifications.unread }) }}
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
          :key="`${entry.event.hostId}-${entry.event.containerId}-${entry.event.occurredAt}`"
          @click="openContainer(entry.event)"
        >
          <div class="flex flex-col gap-0.5 py-1">
            <span class="text-xs text-content">{{ entry.text }}</span>
            <span class="text-[10px] text-content-faint">
              {{ entry.event.hostId }} · {{ entry.event.containerId.slice(0, 12) }}
            </span>
          </div>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>
