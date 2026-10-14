<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/**
 * 操作审计页预告（1.0.0 之后）。
 *
 * 注意这页**不是** Docker Events 流——那是 Docker 引擎自己产生的容器事件，
 * 与「谁在平台上做了什么」是两回事。列定义留在这里只是为了固定最终形态，
 * 数据源要等审计表落地。
 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.events.cap1'),
  t('page.events.cap2'),
  t('page.events.cap3')
])

const columns = computed(() => [
  { label: t('events.col.time') },
  { label: t('events.col.actor') },
  { label: t('events.col.action') },
  { label: t('events.col.target') },
  { label: t('events.col.result') }
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-events"
      :title="t('nav.events')"
      :subtitle="t('page.events.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-events"
        :title="t('state.notImplemented')"
        version="1.0.0+"
        :capabilities="capabilities"
        :note="t('page.events.note')"
      />

      <!-- 最终列定义：数据源就绪后只需接上，表格形态不再变动 -->
      <div class="app-card mt-4 overflow-hidden">
        <div
          class="hidden md:flex border-b border-line-subtle bg-surface-sunken/60"
        >
          <div
            v-for="c in columns"
            :key="c.label"
            class="flex-1 px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-content-muted app-truncate"
          >
            {{ c.label }}
          </div>
        </div>
        <p class="px-4 py-3 text-center text-xs text-content-faint">
          {{ t('page.events.placeholder') }}
        </p>
      </div>
    </div>
  </div>
</template>