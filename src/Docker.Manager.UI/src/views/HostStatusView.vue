<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import type { HostOverview } from '@/types/api'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import StatusDot from '@/components/common/StatusDot.vue'

const route = useRoute()
const { t } = useI18n()
const host = useHostStore()

const currentHost = computed(() => {
  const id = String(route.params.hostId ?? '')
  return host.hosts.find((h) => h.id === id) ?? null
})
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-activity"
      :title="t('nav.status')"
      :subtitle="currentHost?.name"
    />

    <!-- 运行状态 -->
    <SectionCard :title="t('status.running')">
      <template #actions>
        <span class="inline-flex items-center gap-2">
          <StatusDot :status="currentHost?.status" size="md" />
          <span class="text-xs text-gray-500 dark:text-gray-400">
            {{ t('status.uptime', { value: '—' }) }}
          </span>
        </span>
      </template>

      <div class="flex items-center gap-3 flex-wrap">
        <span class="text-sm font-medium text-gray-900 dark:text-gray-100">
          {{ currentHost?.name }}
        </span>
        <span class="text-xs text-gray-400 dark:text-gray-500 font-mono">
          {{ currentHost?.endpoint }}
        </span>
      </div>

      <!-- 资源计数：后端聚合接口就绪前显示占位 -->
      <div class="mt-4 grid grid-cols-2 lg:grid-cols-4 gap-3">
        <div v-for="k in ['containers', 'images', 'volumes', 'networks']" :key="k" class="app-stat-tile">
          <div class="text-xs text-gray-500 dark:text-gray-400">{{ t(`nav.${k}`) }}</div>
          <div class="mt-1 text-xl font-semibold app-tabular text-gray-700 dark:text-gray-300">
            {{ currentHost?.counts ? currentHost.counts[k as keyof HostOverview['counts'] & string] : '—' }}
          </div>
        </div>
      </div>
    </SectionCard>

    <!-- 实时指标：SignalR 就绪前占位 -->
    <SectionCard class="app-section" :title="t('status.realtime')" :description="t('status.realtimeHint')">
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div
          v-for="m in [
            { label: t('status.cpu'), icon: 'i-carbon-meter' },
            { label: t('status.memory'), icon: 'i-carbon-meter--alt' }
          ]"
          :key="m.label"
          class="rounded-lg border border-dashed border-gray-200 dark:border-gray-700 px-4 py-6 text-center"
        >
          <span class="text-xl text-gray-300 dark:text-gray-600" :class="m.icon" />
          <div class="mt-2 text-xs text-gray-500 dark:text-gray-400">{{ m.label }}</div>
          <div class="mt-1 text-2xl font-semibold text-gray-300 dark:text-gray-600">—</div>
        </div>
      </div>
    </SectionCard>
  </div>
</template>