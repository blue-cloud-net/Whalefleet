<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/datetime'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import StatCard from '@/components/common/StatCard.vue'
import HostCard from '@/components/business/HostCard.vue'
import HostGroupSection from '@/components/business/HostGroupSection.vue'

const { t } = useI18n()
const host = useHostStore()
const auth = useAuthStore()

const hosts = computed(() => host.hosts)
/** 按分组聚合（分组顺序由 store 保证，未分组置末） */
const groups = computed(() => host.groupedHosts)

/**
 * 首次加载：出骨架而不是空页面。
 *
 * 主机列表改由 API 加载后，首屏必然有一段空窗；直接渲染「0 台主机」会让人
 * 误以为真的没主机。骨架的形状与真实行一致，避免加载完成时布局跳一下。
 */
const showSkeleton = computed(() => host.loading && hosts.value.length === 0)
/** 加载失败：出错误态 + 重试，而不是「没数据」 */
const showError = computed(() => host.error !== null && hosts.value.length === 0)
/** 已加载且确实为空：才走空状态 */
const showEmpty = computed(
  () => !showSkeleton.value && !showError.value && hosts.value.length === 0
)

const onlineCount = computed(() => hosts.value.filter((h) => h.status === 'online').length)
const offlineCount = computed(() => hosts.value.filter((h) => h.status === 'offline').length)

/** 顶部统计磁贴：总量 / 在线 / 离线；未加载出主机时整体隐藏（否则会闪烁出「0」） */
const stats = computed(() => [
  {
    key: 'total',
    label: t('overview.stat.total'),
    value: hosts.value.length,
    // 早先写的是 `i-carbon-data-center-alt`——Carbon 图标集里没有 `-alt` 变体，
    // 图标不渲染只留一个空底色方块（表现为「主机总数没图标」）。统一用 `data-center`。
    icon: 'i-carbon-data-center',
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
      icon="i-carbon-dashboard"
      :title="t('overview.title')"
      :subtitle="t('overview.subtitle')"
      narrow
    >
      <template #actions>
        <span v-if="host.lastSyncedAt && !showSkeleton" class="text-xs text-content-faint">
          {{ t('state.updatedAt', { time: formatDateTime(host.lastSyncedAt) }) }}
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
    <div class="app-stack">
      <!-- 加载中：形状与真实主机行一致的骨架 -->
      <div v-if="showSkeleton" class="space-y-2.5" aria-busy="true" :aria-label="t('common.loading')">
        <el-skeleton v-for="i in 3" :key="i" animated class="app-card">
          <template #template>
            <div class="flex items-center gap-4 p-4">
              <el-skeleton-item variant="circle" class="!w-2 !h-2" />
              <div class="flex-1 space-y-2">
                <el-skeleton-item variant="text" class="!w-32" />
                <el-skeleton-item variant="text" class="!w-48 !h-3" />
              </div>
              <el-skeleton-item variant="text" class="!w-40" />
            </div>
          </template>
        </el-skeleton>
      </div>

      <!-- 加载失败：错误态 + 重试。绝不退化成「没数据」 -->
      <div v-else-if="showError" class="app-card">
        <EmptyState tone="error" icon="i-carbon-warning-alt" :description="t('state.loadFailed')">
          <el-button :loading="host.loading" @click="host.loadHosts()">
            <span class="i-carbon-renew text-base" />
            {{ t('common.retry') }}
          </el-button>
        </EmptyState>
      </div>

      <div v-else-if="groups.length > 0" class="space-y-7">
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

      <div v-else-if="showEmpty" class="app-card">
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