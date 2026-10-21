<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { useAuthStore } from '@/stores/auth'
import { listHostTags } from '@/api/host-tag'
import { formatDateTime } from '@/utils/datetime'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import StatCard from '@/components/common/StatCard.vue'
import HostCard from '@/components/business/HostCard.vue'
import HostGroupSection from '@/components/business/HostGroupSection.vue'
import type { HostTag } from '@/types/api'

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

/** 过滤后无匹配：文案不是「没有主机」，而是「没有符合条件的主机」。 */
const showFilteredEmpty = computed(() => showEmpty.value && filtering.value)
const showNoHostsEmpty = computed(() => showEmpty.value && !filtering.value)

const onlineCount = computed(() => hosts.value.filter((h) => h.status === 'online').length)
const offlineCount = computed(() => hosts.value.filter((h) => h.status === 'offline').length)

/**
 * 过滤条件（0.6.0，**服务端过滤**）。
 *
 * ⚠️ 「未分组」用**空串**表达：不传与传 `null` 都是「不过滤」（JSON 分不清），
 * 只有空串表示「只看未分组」。
 */
const filter = reactive({
  groupId: '__all__' as string,
  tagIds: [] as string[],
  status: '' as string,
  search: ''
})

/** 全量标签（过滤下拉用）。 */
const tags = ref<HostTag[]>([])

/** 是否处于过滤状态（用于显示「无匹配」而不是「没有主机」）。 */
const filtering = computed(
  () =>
    filter.groupId !== '__all__' ||
    filter.tagIds.length > 0 ||
    filter.status !== '' ||
    filter.search.trim() !== ''
)

/** 把界面状态翻译成服务端参数并重拉。 */
async function applyFilter(): Promise<void> {
  host.setFilter({
    // 空串 = 未分组；下拉的「全部分组」用特殊值 `__all__`，在这里变回 undefined
    groupId: filter.groupId === '__all__' ? undefined : filter.groupId,
    tagIds: filter.tagIds.length > 0 ? filter.tagIds : undefined,
    status: filter.status === '' ? undefined : (filter.status as 'online' | 'offline' | 'unknown'),
    search: filter.search.trim() === '' ? undefined : filter.search.trim()
  })

  await host.loadHosts()
}

/** 清除全部条件。 */
async function clearFilter(): Promise<void> {
  filter.groupId = '__all__'
  filter.tagIds = []
  filter.status = ''
  filter.search = ''
  await applyFilter()
}

onMounted(async () => {
  try {
    tags.value = await listHostTags()
  } catch {
    // 标签列表拿不到时过滤栏退化成「无标签可选」，不该让整页报错
    tags.value = []
  }
})

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

    <!--
      过滤栏（0.6.0）：分组 / 标签 / 状态 / 关键字。
      ⚠️ 「未分组」用空串（`:value="''"`），不要给 `null`——`el-option` 不接受 null 作为 value，
      而且 JSON 也分不清「没给」与「给了 null」。
    -->
    <div class="app-card mb-4 flex flex-wrap items-center gap-2 px-3 py-2.5">
      <el-select
        v-model="filter.groupId"
        size="small"
        class="w-40"
        @change="applyFilter"
      >
        <el-option :label="t('overview.filter.all')" value="__all__" />
        <el-option :label="t('overview.filter.ungrouped')" value="" />
        <el-option v-for="group in host.groups" :key="group.id" :label="group.name" :value="group.id" />
      </el-select>

      <el-select
        v-model="filter.tagIds"
        size="small"
        multiple
        collapse-tags
        class="w-44"
        :placeholder="t('overview.filter.allTags')"
        @change="applyFilter"
      >
        <el-option v-for="tag in tags" :key="tag.id" :label="tag.name" :value="tag.id" />
      </el-select>

      <el-select v-model="filter.status" size="small" class="w-32" @change="applyFilter">
        <el-option :label="t('overview.filter.status')" value="" />
        <el-option label="Online" value="online" />
        <el-option label="Offline" value="offline" />
        <el-option label="Unknown" value="unknown" />
      </el-select>

      <el-input
        v-model="filter.search"
        size="small"
        clearable
        class="w-52"
        :placeholder="t('overview.filter.search')"
        @keyup.enter="applyFilter"
        @clear="applyFilter"
      />

      <el-button v-if="filtering" size="small" text @click="clearFilter">
        <span class="i-carbon-close text-sm" />
        {{ t('overview.filter.clear') }}
      </el-button>
    </div>
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
    <!--
      用 `app-section`（mt-6 sm:mt-8）而不是 `app-stack`：这里需要的是「统计条」与
      「分组列表」两个**区块**之间的间距，而 `app-stack` 的 gap-4 只在同一 flex 容器
      的多个子元素之间生效——下面的骨架/错误/分组/空状态是 v-if 互斥分支，同时只会
      渲染一个，gap 永远不生效，统计条与首个分组标题因此贴在一起。
    -->
    <div class="app-section">
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

      <div v-else-if="showFilteredEmpty" class="app-card">
        <EmptyState icon="i-carbon-filter" :description="t('overview.filter.empty')">
          <el-button @click="clearFilter">
            <span class="i-carbon-close text-base" />
            {{ t('overview.filter.clear') }}
          </el-button>
        </EmptyState>
      </div>

      <div v-else-if="showNoHostsEmpty" class="app-card">
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