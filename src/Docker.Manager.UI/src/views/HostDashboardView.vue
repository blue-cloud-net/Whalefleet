<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { formatUptime, formatDateTime, type DurationUnits } from '@/utils/datetime'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import StatCard from '@/components/common/StatCard.vue'
import StatusDot from '@/components/common/StatusDot.vue'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const host = useHostStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const currentHost = computed(() => host.hosts.find((h) => h.id === hostId.value) ?? null)

/** 运行时长的单位文案走 i18n */
const durationUnits = computed<DurationUnits>(() => ({
  day: t('host.duration.day'),
  hour: t('host.duration.hour'),
  minute: t('host.duration.minute'),
  second: t('host.duration.second')
}))

/**
 * 主机信息行：字段缺失时整行省略，不显示占位。
 * Agent 版本仅在后端返回时才出现（Agent 模式排在 1.0.0 之后）。
 */
const hostInfoRows = computed(() => {
  const h = currentHost.value
  if (!h) return []

  const rows: { label: string; value: string }[] = []
  const v = h.version

  if (v?.version) rows.push({ label: t('host.info.version'), value: v.version })
  if (v?.apiVersion) rows.push({ label: t('host.info.apiVersion'), value: v.apiVersion })
  if (v?.os) rows.push({ label: t('host.info.os'), value: v.arch ? `${v.os}/${v.arch}` : v.os })
  if (v?.goVersion) rows.push({ label: t('host.info.goVersion'), value: v.goVersion })
  if (h.startedAt) {
    rows.push({
      label: t('host.info.startedAt'),
      value: `${formatDateTime(h.startedAt)} · ${t('status.uptime', {
        value: formatUptime(h.startedAt, durationUnits.value)
      })}`
    })
  }
  // 预留：Agent 模式 1.0.0 之后，届时后端返回 agentVersion
  if (h.agentVersion) rows.push({ label: t('host.info.agentVersion'), value: h.agentVersion })

  return rows
})

type ResourceKey = 'containers' | 'images' | 'volumes' | 'networks'

const stats = computed<{ key: ResourceKey; label: string; icon: string }[]>(() => [
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-container-software' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-image' },
  { key: 'volumes', label: t('nav.volumes'), icon: 'i-carbon-data-volume' },
  { key: 'networks', label: t('nav.networks'), icon: 'i-carbon-network-3' }
])

function countOf(key: ResourceKey): number | string {
  const counts = currentHost.value?.counts
  if (!counts) return '—'
  return counts[key]
}

function go(key: ResourceKey) {
  router.push({ name: `host-${key}`, params: { hostId: hostId.value } })
}
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-cloud-satellite"
      :title="currentHost?.name ?? hostId"
      :subtitle="currentHost?.endpoint"
    >
      <template #badge>
        <span class="app-chip" :class="`ring-current/20`">
          <StatusDot :status="currentHost?.status" size="sm" />
          <span class="text-gray-600 dark:text-gray-300">
            {{ currentHost?.status === 'offline' ? t('host.status.offline') : t('host.status.online') }}
          </span>
        </span>
      </template>
    </PageHeader>

    <!-- 资源计数：点击直达该资源页 -->
    <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
      <button
        v-for="s in stats"
        :key="s.key"
        class="app-card app-card-interactive p-4 text-left"
        @click="go(s.key)"
      >
        <div class="flex items-center gap-2.5">
          <span
            class="w-8 h-8 rounded-md grid place-items-center text-base shrink-0 bg-brand-500/10 text-brand-600 dark:text-brand-400"
          >
            <span :class="s.icon" />
          </span>
          <span class="text-xs font-medium text-gray-500 dark:text-gray-400 app-truncate">
            {{ s.label }}
          </span>
        </div>
        <div class="mt-2.5 text-2xl font-semibold leading-none app-tabular text-gray-900 dark:text-gray-100">
          {{ countOf(s.key) }}
        </div>
      </button>
    </div>

    <!-- 主机信息：版本 / 启动时间；字段缺失时整块省略 -->
    <SectionCard
      v-if="hostInfoRows.length > 0"
      class="app-section"
      :title="t('host.info.title')"
    >
      <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-8 gap-y-3.5">
        <div
          v-for="row in hostInfoRows"
          :key="row.label"
          class="flex items-baseline justify-between gap-4 pb-2 border-b border-dashed border-gray-200 dark:border-gray-700/60 last:border-0"
        >
          <dt class="text-xs text-gray-500 dark:text-gray-400 shrink-0">{{ row.label }}</dt>
          <dd
            class="text-sm text-gray-800 dark:text-gray-200 text-right app-truncate font-mono text-[13px]"
            :title="row.value"
          >
            {{ row.value }}
          </dd>
        </div>
      </dl>
    </SectionCard>
  </div>
</template>