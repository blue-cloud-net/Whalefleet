<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { HostOverview } from '@/types/api'
import { formatUptime, type DurationUnits } from '@/utils/datetime'

const props = defineProps<{
  host: HostOverview
}>()

const { t } = useI18n()

/** 运行时长的单位文案走 i18n，避免在 utils 里硬编码语言 */
const units = computed<DurationUnits>(() => ({
  day: t('host.duration.day'),
  hour: t('host.duration.hour'),
  minute: t('host.duration.minute'),
  second: t('host.duration.second')
}))

const uptime = computed(() => formatUptime(props.host.startedAt, units.value))

/** 四类资源计数；后端聚合接口就绪前为 undefined */
const stats = computed(() => [
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-container-software' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-image' },
  { key: 'volumes', label: t('nav.volumes'), icon: 'i-carbon-data-volume' },
  { key: 'networks', label: t('nav.networks'), icon: 'i-carbon-network-3' }
])

/**
 * CPU / 内存等实时指标依赖 SignalR 推送（0.3.0），
 * 当前仅展示占位，等 `/hubs/container-stats` 就绪后替换为真实图表。
 */
const realtimeStats = computed(() => [
  { label: t('status.cpu'), icon: 'i-carbon-meter', value: null as string | null },
  { label: t('status.memory'), icon: 'i-carbon-meter--alt', value: null as string | null }
])

const statusColor = computed(() => {
  switch (props.host.status) {
    case 'online':
      return 'bg-green-500'
    case 'offline':
      return 'bg-red-500'
    default:
      return 'bg-gray-400'
  }
})

function countOf(key: 'containers' | 'images' | 'volumes' | 'networks'): number | string {
  const counts = props.host.counts
  return counts ? counts[key] : '—'
}
</script>

<template>
  <div class="space-y-4">
    <!-- 运行状态 -->
    <section class="app-card p-5">
      <div class="flex items-center justify-between gap-3 flex-wrap">
        <div class="flex items-center gap-2">
          <span class="w-2.5 h-2.5 rounded-full shrink-0" :class="statusColor" />
          <h2 class="text-sm font-semibold text-gray-900 dark:text-gray-100">
            {{ t('status.running') }}
          </h2>
        </div>
        <span class="text-xs text-gray-500 dark:text-gray-400">
          {{ t('status.uptime', { value: uptime }) }}
        </span>
      </div>

      <!-- 资源计数 -->
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-3 mt-4">
        <div
          v-for="s in stats"
          :key="s.key"
          class="rounded-md bg-gray-50 dark:bg-gray-800 px-3 py-3"
        >
          <div class="flex items-center gap-1.5">
            <span class="text-base text-gray-400 dark:text-gray-500" :class="s.icon" />
            <span class="text-xs text-gray-500 dark:text-gray-400">{{ s.label }}</span>
          </div>
          <div class="mt-1.5 text-xl font-semibold text-gray-700 dark:text-gray-300">
            {{ countOf(s.key) }}
          </div>
        </div>
      </div>
    </section>

    <!-- 实时指标：SignalR 就绪前占位 -->
    <section class="app-card p-5">
      <h2 class="text-sm font-semibold text-gray-900 dark:text-gray-100">
        {{ t('status.realtime') }}
      </h2>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 mt-4">
        <div
          v-for="m in realtimeStats"
          :key="m.label"
          class="rounded-md border border-dashed border-gray-200 dark:border-gray-700 px-3 py-4"
        >
          <div class="flex items-center gap-1.5">
            <span class="text-base text-gray-400 dark:text-gray-500" :class="m.icon" />
            <span class="text-xs text-gray-500 dark:text-gray-400">{{ m.label }}</span>
          </div>
          <div class="mt-1.5 text-xl font-semibold text-gray-300 dark:text-gray-600">—</div>
        </div>
      </div>
      <p class="mt-3 text-xs text-gray-400 dark:text-gray-500">{{ t('status.realtimeHint') }}</p>
    </section>
  </div>
</template>