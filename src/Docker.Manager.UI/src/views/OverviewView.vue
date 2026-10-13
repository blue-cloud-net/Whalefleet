<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { useAuthStore } from '@/stores/auth'
import EmptyState from '@/components/common/EmptyState.vue'
import HostCard from '@/components/business/HostCard.vue'
import HostGroupSection from '@/components/business/HostGroupSection.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import StatCard from '@/components/common/StatCard.vue'

const { t } = useI18n()
const host = useHostStore()
const auth = useAuthStore()

const hosts = computed(() => host.hosts)
/** 按分组聚合（分组顺序由 store 保证，未分组置末） */
const groups = computed(() => host.groupedHosts)

const onlineCount = computed(() => hosts.value.filter((h) => h.status === 'online').length)
const offlineCount = computed(() => hosts.value.filter((h) => h.status === 'offline').length)

/** 顶部统计磁贴：总量 / 在线 / 离线，未加载主机时整体隐藏 */
const stats = computed(() => [
  {
    key: 'total',
    label: t('overview.stat.total'),
    value: hosts.value.length,
    icon: 'i-carbon-data-center-alt',
    tone: 'brand' as const
  },
  {
    key: 'online',
    label: t('overview.stat.online'),
    value: onlineCount.value,
    icon: 'i-carbon-checkmark-filled',
    tone: 'success' as const
  },
  {
    key: 'offline',
    label: t('overview.stat.offline'),
    value: offlineCount.value,
    icon: 'i-carbon-warning',
    tone: offlineCount.value > 0 ? ('danger' as const) : ('neutral' as const)
  }
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-data-center-alt"
      :title="t('overview.title')"
      :subtitle="t('overview.subtitle')"
      narrow
    >
      <template #actions>
        <span class="text-sm text-gray-500 dark:text-gray-400">
          {{ t('overview.hostCount', { total: hosts.length, online: onlineCount }) }}
        </span>
      </template>
    </PageHeader>

    <!-- 统计条 -->
    <div v-if="hosts.length > 0" class="grid grid-cols-1 sm:grid-cols-3 gap-3">
      <StatCard
        v-for="s in stats"
        :key="s.key"
        :icon="s.icon"
        :label="s.label"
        :value="s.value"
        :tone="s.tone"
      />
    </div>

    <!-- 分组主机列表 -->
    <div :class="hosts.length > 0 ? 'app-section' : ''">
      <div v-if="groups.length > 0" class="space-y-7">
        <HostGroupSection
          v-for="(g, i) in groups"
          :key="g.group?.id ?? `__ungrouped_${i}`"
          :group="g.group"
          :count="g.hosts.length"
        >
          <div class="space-y-2.5">
            <HostCard v-for="h in g.hosts" :key="h.id" :host="h" />
          </div>
        </HostGroupSection>
      </div>

      <div v-else class="app-card">
        <EmptyState icon="i-carbon-cloud-offline" :description="t('overview.noHosts')">
          <el-button v-if="auth.isAdmin" type="primary" @click="$router.push({ name: 'hosts' })">
            <span class="i-carbon-add text-base" />
            {{ t('overview.manageHosts') }}
          </el-button>
        </EmptyState>
      </div>
    </div>
  </div>
</template>