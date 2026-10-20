<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'

/**
 * 主机「状态」页。
 *
 * 与概览页的职责切分：**概览 = 这台机器是谁（身份、端点、版本、资源数量）；
 * 状态 = 它现在怎么样（实时指标、近期事件）**。
 *
 * 早先这一页把「运行状态 + 四类资源计数 + 运行时长」又抄了一遍，与概览页几乎
 * 逐字重复，用户在侧栏点「状态」看到的还是那四个破折号。这里把那些搬回概览页，
 * 本页只保留真正属于「实时性」的两块。
 */
const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const host = useHostStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const currentHost = computed(
  () => host.hosts.find((h) => h.id === hostId.value) ?? null
)

/**
 * 实时指标磁贴。
 *
 * **宿主机级 CPU / 内存 / 网络是「不支持」而不是「还没做」**：Docker Engine API
 * 没有宿主机级指标端点——`/info` 只给 `NCPU` / `MemTotal` 这样的静态上限，
 * `docker stats` 只针对容器。用容器聚合值冒充会让同一页出现两套口径，比不给更糟。
 * 容器级指标在容器列表与详情页。
 *
 * 磁盘用量走 `system/df`，排在 0.6.0，因此仍是虚线占位。
 */
const metrics = computed<{ label: string; icon: string; supported: boolean }[]>(
  () => [
    { label: t('status.cpu'), icon: 'i-carbon-meter', supported: false },
    // 早先误写成 `i-carbon-meter--alt`（双连字符），Carbon 里只有 `meter-alt`。
    { label: t('status.memory'), icon: 'i-carbon-meter-alt', supported: false },
    { label: t('status.network'), icon: 'i-carbon-network-4', supported: false },
    { label: t('status.disk'), icon: 'i-carbon-data-volume', supported: true }
  ]
)

function goEvents() {
  router.push({ name: 'host-events', params: { hostId: hostId.value } })
}
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-activity"
      :title="t('nav.status')"
      :subtitle="currentHost?.name ?? hostId"
      narrow
    />

    <!-- 实时指标：宿主机级 CPU / 内存 / 网络**不支持**；磁盘待 0.6.0 的 system/df -->
    <SectionCard :title="t('status.realtime')" :description="t('status.realtimeHint')">
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <div
          v-for="m in metrics"
          :key="m.label"
          class="rounded-lg border border-dashed border-line-subtle px-3 py-5 text-center"
        >
          <span class="text-xl text-content-faint" :class="m.icon" aria-hidden="true" />
          <div class="mt-2 text-xs text-content-muted">{{ m.label }}</div>
          <div
            class="mt-1 font-semibold leading-none app-tabular"
            :class="m.supported ? 'text-2xl text-content-faint' : 'text-sm text-content-faint'"
          >
            {{ m.supported ? '—' : t('status.unsupported') }}
          </div>
        </div>
      </div>
    </SectionCard>

    <!-- 近期事件：尚未实现，给出明确去向而不是一片空白 -->
    <SectionCard class="app-section" :title="t('status.events')">
      <div class="py-6 text-center">
        <span class="i-carbon-events text-3xl text-content-faint" aria-hidden="true" />
        <p class="mt-3 text-sm text-content-muted">{{ t('status.eventsPending') }}</p>
        <p class="mx-auto mt-1.5 max-w-md text-xs text-content-faint">
          {{ t('status.eventsHint') }}
        </p>
        <el-button class="!mt-4" @click="goEvents">
          <span class="i-carbon-arrow-right text-sm" />
          {{ t('nav.events') }}
        </el-button>
      </div>
    </SectionCard>
  </div>
</template>