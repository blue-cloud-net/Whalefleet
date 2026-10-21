<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useHostStore } from '@/stores/host'
import type { HostOverview } from '@/types/api'
import { formatBytes, type ByteUnits } from '@/utils/container-status'
import HostHero from '@/components/business/HostHero.vue'
import ResourceTile from '@/components/business/ResourceTile.vue'
import SectionCard from '@/components/common/SectionCard.vue'

/**
 * 主机落地页（`/hosts/:hostId/dashboard`）。
 *
 * 职责刻意收窄为**这台机器的身份与技术信息**：连接端点、运行时长、Docker 版本，
 * 以及四类资源计数作为通往各资源页的入口。实时指标与事件归「状态」页，
 * 两页不再重复同一块内容（0.0.7 时它们几乎一模一样）。
 *
 * 这一页此前的问题不是样式，而是**缺失态没有被设计**：`counts` / `version` 缺失时
 * 信息卡整块不渲染、计数位只剩一个裸「—」，下半屏全白，看起来像加载失败。
 * 现在信息卡恒显、缺失字段显示弱化的占位符并在末尾给出统一说明。
 */
const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const host = useHostStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const currentHost = computed<HostOverview | null>(
  () => host.hosts.find((h) => h.id === hostId.value) ?? null
)

type ResourceKey = 'containers' | 'images' | 'volumes' | 'networks'

/** 四类资源磁贴的图标与文案；顺序即页面顺序 */
const resources = computed<{ key: ResourceKey; label: string; icon: string }[]>(() => [
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-container-software' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-image' },
  { key: 'volumes', label: t('nav.volumes'), icon: 'i-carbon-data-volume' },
  { key: 'networks', label: t('nav.networks'), icon: 'i-carbon-network-3' }
])

/** 计数缺失（离线或聚合接口未就绪）时给磁贴一句原因，避免「—」被误读成「零」 */
const missingHint = computed(() =>
  currentHost.value?.status === 'offline'
    ? t('host.counts.unavailable')
    : t('host.info.pending')
)

function countOf(key: ResourceKey): number | null {
  return currentHost.value?.counts?.[key] ?? null
}

/**
 * 容器磁贴的状态明细：运行 / 停止 / 健康 / 不健康。
 *
 * 与「容器总数」并列显示而不是塞进 tooltip：这四个数字回答的是不同问题——
 * 运行 + 停止 = 总量（这台机器上有没有挂掉的），健康 + 不健康只覆盖**配了健康检查**
 * 的那部分（服务真的还活着吗）。只看总数会把「37 个都在跑，但 1 个已经不健康」
 * 完全藏起来。
 *
 * 计数缺失（离线主机 / 概览未取到）时返回 null，磁贴退回「—」占位，不显示一排 0。
 */
const containerBreakdown = computed<
  { key: string; icon: string; tone: string; value: number; label: string }[] | null
>(() => {
  const counts = currentHost.value?.counts
  if (!counts) return null

  return [
    {
      key: 'running',
      icon: 'i-carbon-power',
      tone: 'text-status-ok',
      value: counts.containersRunning,
      label: t('container.state.running')
    },
    {
      key: 'stopped',
      icon: 'i-carbon-power',
      tone: 'text-status-down',
      value: counts.containersStopped,
      label: t('container.state.stopped')
    },
    {
      key: 'healthy',
      icon: 'i-carbon-favorite',
      tone: 'text-status-ok',
      value: counts.containersHealthy,
      label: t('container.health.healthy')
    },
    {
      key: 'unhealthy',
      icon: 'i-carbon-favorite',
      tone: 'text-status-down',
      value: counts.containersUnhealthy,
      label: t('container.health.unhealthy')
    }
  ]
})

/** 字节单位走 i18n：与镜像 / 卷体积的展示口径一致 */
const byteUnits = computed<ByteUnits>(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

/**
 * 镜像磁贴的副信息：镜像层占用的磁盘空间。
 *
 * 只显示大小、不写「磁盘占用」四个字：磁贴本身就在「镜像」下面，右侧的数字只可能是
 * 体积；多一行文案反而要挤占宽度。单位已自带语义，无须再解释。
 *
 * 取 `null`（守护进程查不到）时交给 `formatBytes` 显示「—」——**不能显示 0**：
 * 0 字节会被读成「这台机器上没有镜像」。
 */
const imagesDiskUsage = computed(() => {
  const counts = currentHost.value?.counts
  if (!counts) return null

  return formatBytes(counts.imagesSizeBytes, byteUnits.value)
})

function goResource(key: ResourceKey) {
  router.push({ name: `host-${key}`, params: { hostId: hostId.value } })
}

/**
 * 刷新：主机列表目前来自本地种子（0.0.8 的列表接口就绪后才真正拉取），
 * 这里沿用与「编辑主机」一致的处理方式——明确告知未就绪，而不是点一下没反应。
 */
function onRefresh() {
  ElMessage.info(t('host.hero.refreshSoon'))
}

/** 缺失值的统一占位符，避免各处各写一个长得不一样的破折号 */
const MISSING = '—'

/**
 * 主机信息行：**恒显**，缺字段用占位符而非跳过整行。
 *
 * 早先的写法是「字段缺失就整行不 push」，于是离线主机这块直接消失、
 * 页面只剩四个破折号。现在缺失以弱化占位呈现，并在卡片末尾统一说明原因。
 */
const infoRows = computed<{ label: string; value: string }[]>(() => {
  const h = currentHost.value
  if (!h) return []

  const rows: { label: string; value: string }[] = []
  const v = h.version

  rows.push({ label: t('host.info.version'), value: v?.version ?? MISSING })
  rows.push({ label: t('host.info.apiVersion'), value: v?.apiVersion ?? MISSING })
  rows.push({
    label: t('host.info.os'),
    value: v?.os ? (v.arch ? `${v.os}/${v.arch}` : v.os) : MISSING
  })
  rows.push({ label: t('host.info.goVersion'), value: v?.goVersion ?? MISSING })
  // 0.6.0 起不再展示「启动时间」：Docker Engine API 不提供守护进程启动时刻
  // （`/info` 的 SystemTime 是**当前时间**），旧字段恒为「已运行 0 秒」。
  // 预留：Agent 模式排在 1.0.0 之后，届时后端才会返回 agentVersion
  if (h.agentVersion) rows.push({ label: t('host.info.agentVersion'), value: h.agentVersion })

  return rows
})

/**
 * 守护进程静态信息（0.6.0）。
 *
 * ⚠️ **CPU / 内存是静态上限**：Docker Engine API 没有宿主机级实时指标端点。
 * 界面必须在标题上把这一点说清楚，否则「24 核」会被读成「当前用了 24 核」。
 */
const daemonRows = computed(() => {
  const d = currentHost.value?.daemon
  if (!d) return []

  const rows: { label: string; value: string }[] = []

  if (d.operatingSystem || d.osVersion || d.osType) {
    rows.push({
      label: t('host.daemon.os'),
      value: [d.operatingSystem, d.osVersion, d.osType].filter(Boolean).join(' · ')
    })
  }

  if (d.kernelVersion) rows.push({ label: t('host.daemon.kernel'), value: d.kernelVersion })

  // 静态上限：带「核」「内存总量」字样，不写「当前」
  if (d.cpuCount > 0) rows.push({ label: t('host.daemon.cpus'), value: String(d.cpuCount) })
  if (d.memoryBytes > 0) {
    rows.push({ label: t('host.daemon.memory'), value: formatBytes(d.memoryBytes, byteUnits.value) })
  }

  if (d.storageDriver) {
    rows.push({
      label: t('host.daemon.storage'),
      value: d.storageDriverStatus.length > 0
        ? `${d.storageDriver} (${d.storageDriverStatus.map((pair) => pair.join('=')).join(', ')})`
        : d.storageDriver
    })
  }

  if (d.loggingDriver) rows.push({ label: t('host.daemon.logging'), value: d.loggingDriver })
  if (d.cgroupDriver) {
    rows.push({
      label: t('host.daemon.cgroup'),
      value: d.cgroupVersion ? `${d.cgroupDriver} v${d.cgroupVersion}` : d.cgroupDriver
    })
  }
  if (d.defaultRuntime) {
    rows.push({
      label: t('host.daemon.runtime'),
      value: d.runtimes.length > 0
        ? `${d.defaultRuntime} (${d.runtimes.join(', ')})`
        : d.defaultRuntime
    })
  }

  if (d.containerdVersion || d.runcVersion) {
    rows.push({
      label: t('host.daemon.components'),
      value: [
        d.containerdVersion ? `containerd ${d.containerdVersion}` : null,
        d.runcVersion ? `runc ${d.runcVersion}` : null
      ].filter(Boolean).join(' · ')
    })
  }

  if (d.registryMirrors.length > 0) {
    rows.push({ label: t('host.daemon.mirrors'), value: d.registryMirrors.join(', ') })
  }

  if (d.httpProxy || d.httpsProxy || d.noProxy) {
    // 代理地址的 userinfo 已由服务端遮蔽，这里直接展示
    rows.push({
      label: t('host.daemon.proxy'),
      value: [
        d.httpProxy ? `HTTP ${d.httpProxy}` : null,
        d.httpsProxy ? `HTTPS ${d.httpsProxy}` : null,
        d.noProxy ? `NO_PROXY ${d.noProxy}` : null
      ].filter(Boolean).join(' · ')
    })
  }

  if (d.dockerRootDir) rows.push({ label: t('host.daemon.rootDir'), value: d.dockerRootDir })

  return rows
})

/** 只要有任意字段缺失，就在卡片末尾给出统一说明 */
const hasMissingInfo = computed(() => infoRows.value.some((r) => r.value === MISSING))
</script>

<template>
  <div class="app-stack">
    <HostHero :host="currentHost" :host-id="hostId" @refresh="onRefresh" />

    <!-- 四类资源：点击直达对应资源页 -->
    <div class="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-3">
      <ResourceTile
        v-for="r in resources"
        :key="r.key"
        :icon="r.icon"
        :label="r.label"
        :value="countOf(r.key)"
        :missing-hint="missingHint"
        @click="goResource(r.key)"
      >
        <!--
          容器磁贴的副信息是 2×2 明细（运行 / 停止 / 健康 / 不健康），排在数值**右侧**：
          左边是「一共多少」，右边是「分别什么状态」，横向读才连得起来。
        -->
        <template v-if="r.key === 'containers' && containerBreakdown" #side>
          <span class="grid grid-cols-2 gap-x-3 gap-y-1 border-l border-line-subtle pl-3">
            <span
              v-for="item in containerBreakdown"
              :key="item.key"
              class="flex min-w-0 items-center gap-1"
            >
              <span
                class="shrink-0 text-xs leading-none"
                :class="[item.icon, item.tone]"
                aria-hidden="true"
              />
              <span class="text-xs font-semibold app-tabular text-content-body">
                {{ item.value }}
              </span>
              <span class="app-truncate text-xs">{{ item.label }}</span>
            </span>
          </span>
        </template>

        <!-- 镜像磁贴：右侧只给大小（单位自带语义，不写「磁盘占用」） -->
        <template v-else-if="r.key === 'images' && imagesDiskUsage" #side>
          <span
            class="flex items-center gap-1.5 border-l border-line-subtle pl-3 text-xs text-content-muted"
          >
            <span class="i-carbon-data-base shrink-0 text-sm text-content-faint" aria-hidden="true" />
            <span class="whitespace-nowrap app-tabular">{{ imagesDiskUsage }}</span>
          </span>
        </template>
      </ResourceTile>
    </div>

    <!-- 技术信息 -->
    <SectionCard v-if="currentHost" :title="t('host.info.title')" density="dense">
      <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-10">
        <!--
          标签定宽、值紧随其后（而非 justify-between 右对齐）：
          两列布局下每列约 520px，右对齐会让「标签」到「值」隔出四百多像素，
          眼睛必须横跨整列才能把两者关联起来。这里按定义列表的常规形态排布。
        -->
        <div
          v-for="row in infoRows"
          :key="row.label"
          class="flex items-baseline gap-4 py-2.5 border-b border-dashed border-line-subtle last:border-0"
        >
          <dt class="w-24 sm:w-28 shrink-0 text-xs text-content-muted">{{ row.label }}</dt>
          <dd
            class="min-w-0 text-[13px] font-mono app-truncate"
            :class="row.value === MISSING ? 'text-content-faint' : 'text-content-body'"
            :title="row.value === MISSING ? t('host.info.pending') : row.value"
          >
            {{ row.value }}
          </dd>
        </div>
      </dl>

      <p v-if="hasMissingInfo" class="mt-3 flex items-start gap-1.5 text-xs text-content-faint">
        <span class="i-carbon-information shrink-0 mt-px" aria-hidden="true" />
        <span>{{ t('host.info.pending') }}</span>
      </p>
    </SectionCard>

    <!--
      守护进程信息（0.6.0）：全部来自同一次 /info，不新增调用。
      ⚠️ 标题里必须写「静态上限」：Docker Engine API 没有宿主机级实时指标端点，
      把 24 核读成「当前用了 24 核」是这套信息最容易造成的误解。
    -->
    <SectionCard
      v-if="daemonRows.length > 0"
      class="app-section"
      :title="t('host.daemon.title')"
      :description="t('host.daemon.hint')"
      density="dense"
    >
      <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-10">
        <div
          v-for="row in daemonRows"
          :key="row.label"
          class="flex items-baseline gap-4 py-2.5 border-b border-dashed border-line-subtle last:border-0"
        >
          <dt class="w-24 sm:w-28 shrink-0 text-xs text-content-muted">{{ row.label }}</dt>
          <dd class="min-w-0 text-[13px] font-mono app-truncate text-content-body" :title="row.value">
            {{ row.value }}
          </dd>
        </div>
      </dl>
    </SectionCard>
  </div>
</template>