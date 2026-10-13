<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()

/**
 * 操作审计日志的表格骨架。
 *
 * 列头先按最终形态定死，等后端审计表就绪后只需填数据源。
 * 审计日志在 docs/features.md 中明确排在 1.0.0 之后，当前无后端接口。
 */
const columns = [
  { prop: 'time', label: 'events.col.time', width: 180 },
  { prop: 'actor', label: 'events.col.actor', width: 140 },
  { prop: 'action', label: 'events.col.action', width: 160 },
  { prop: 'target', label: 'events.col.target' },
  { prop: 'result', label: 'events.col.result', width: 120 }
]
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-events" :title="t('nav.events')" :subtitle="t('events.subtitle')" narrow />

    <div class="app-card mt-6 overflow-hidden">
      <!-- 表头：数据就绪前只展示列定义，不渲染空行 -->
      <div class="hidden md:flex border-b border-gray-200 dark:border-gray-700 bg-gray-50/60 dark:bg-gray-800/40">
        <div
          v-for="c in columns"
          :key="c.prop"
          class="px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400 app-truncate"
          :class="c.width ? '' : 'flex-1'"
          :style="c.width ? { width: c.width + 'px' } : undefined"
        >
          {{ t(c.label) }}
        </div>
      </div>

      <EmptyState compact icon="i-carbon-events" :description="t('page.events.placeholder')">
        <p class="max-w-md text-xs text-gray-400 dark:text-gray-500">
          {{ t('events.hint') }}
        </p>
      </EmptyState>
    </div>
  </div>
</template>