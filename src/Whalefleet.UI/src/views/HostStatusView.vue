<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { getHostDiskUsage } from '@/api/host'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import { formatBytes } from '@/utils/container-status'
import type { ByteUnits } from '@/utils/container-status'
import type { HostDiskUsage } from '@/types/api'

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
    { label: t('status.network'), icon: 'i-carbon-network-4', supported: false }
  ]
)

/**
 * 磁盘用量（0.6.0）：四类资源的占用与可回收量。
 *
 * `null` 表示「守护进程没给」（与「确实是 0」不同），界面必须显示「—」。
 */
const disk = ref<HostDiskUsage | null>(null)
const diskLoading = ref(false)
const diskError = ref<string | null>(null)

const byteUnits = computed<ByteUnits>(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

const diskBuckets = computed(() => {
  const value = disk.value
  if (!value) return []

  const buckets = [
    { key: 'images', label: t('status.diskUsage.images'), bucket: value.images },
    { key: 'containers', label: t('status.diskUsage.containers'), bucket: value.containers },
    { key: 'volumes', label: t('status.diskUsage.volumes'), bucket: value.volumes },
    { key: 'buildCache', label: t('status.diskUsage.buildCache'), bucket: value.buildCache }
  ]

  return buckets.map((item) => ({
    ...item,
    size: item.bucket ? formatBytes(item.bucket.totalSize, byteUnits.value) : '—',
    reclaimable: item.bucket?.reclaimable
      ? t('status.diskUsage.reclaimable', {
          value: formatBytes(item.bucket.reclaimable, byteUnits.value)
        })
      : null
  }))
})

async function loadDiskUsage(): Promise<void> {
  if (!hostId.value) return

  diskLoading.value = true
  diskError.value = null

  try {
    disk.value = await getHostDiskUsage(hostId.value)
  } catch (err) {
    diskError.value = err instanceof Error ? err.message : String(err)
  } finally {
    diskLoading.value = false
  }
}

onMounted(() => {
  void loadDiskUsage()
})

// 切换主机时重拉：否则会把上一台机器的数字留在页面上
watch(hostId, () => {
  void loadDiskUsage()
})

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

    <!--
      磁盘用量（0.6.0）：镜像 / 容器 / 卷 / 构建缓存。
      `null` 表示守护进程没给（与「确实是 0」不同），显示「—」。
    -->
    <SectionCard
      class="app-section"
      :title="t('status.diskUsage')"
      :description="t('status.diskUsage.hint')"
      density="dense"
    >
      <div v-loading="diskLoading" class="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <div
          v-for="item in diskBuckets"
          :key="item.key"
          class="rounded-lg border border-line-subtle px-3 py-4"
        >
          <div class="text-xs text-content-muted">{{ item.label }}</div>
          <div class="mt-1 text-lg font-semibold app-tabular text-content-strong">
            {{ item.size }}
          </div>
          <div v-if="item.reclaimable" class="mt-0.5 text-xs text-content-faint">
            {{ item.reclaimable }}
          </div>
        </div>
      </div>

      <p v-if="diskError" class="mt-3 text-sm text-status-down">
        {{ t('status.diskUsage.failed') }}：{{ diskError }}
      </p>
    </SectionCard>

    <!-- 近期事件：尚未实现，给出明确去向而不是一片空白 -->
    <SectionCard class="app-section" :title="t('status.events')">      <div class="py-6 text-center">
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