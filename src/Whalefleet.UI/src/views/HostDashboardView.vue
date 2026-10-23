<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useHostStore } from '@/stores/host'
import type { HostOverview } from '@/types/api'
import { formatBytes, type ByteUnits } from '@/utils/container-status'
import { formatDateTime } from '@/utils/datetime'
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
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-cube' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-archive' },
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
 * 刷新：重新拉取主机列表。
 *
 * 列表本来就走真接口（`POST /api/v1/hosts/list`，见 store 的 `loadHosts()`），
 * `loadHosts()` 会连带重取版本 / 计数（`loadHostDetails()`），因此刷新后
 * 本页的四个磁贴与信息卡都会跟着变。
 *
 * 失败时 store **不抛异常**而是把错误写进 `error` 并保留旧数据，
 * 页面上又没有任何一处渲染它——不提示的话，用户面对一个毫无变化的页面
 * 只会以为按钮坏了，因此这里补一条失败提示。
 */
async function onRefresh(): Promise<void> {
  // 已经在拉了就直接返回：主机多时一次列表往返要数秒，连点会把请求堆起来
  if (host.loading) return

  await host.loadHosts()

  if (host.error !== null) {
    ElMessage({ type: 'error', message: t('state.loadFailed'), showClose: true })
    return
  }

  // 成功也报一声：刷新按钮没有任何视觉状态（`HostHero` 只有一个图标），
  // 不提示的话「点了到底生效没」无从判断。用同步时间当作回执。
  ElMessage({
    type: 'success',
    message: t('state.updatedAt', { time: formatDateTime(host.lastSyncedAt) })
  })
}

/** 缺失值的统一占位符，避免各处各写一个长得不一样的破折号 */
const MISSING = '—'

/** 一行「标签 → 值」 */
type InfoRow = { label: string; value: string }

/** 一组行；`title` 用于在同一张卡里区分内容层次（如「仓库与安全」） */
interface InfoGroup {
  title?: string
  rows: InfoRow[]
}

/** 一张信息卡 */
interface InfoSection {
  key: string
  title: string
  description?: string
  groups: InfoGroup[]
  /** 末尾给出「字段尚未同步」的统一说明 */
  pendingNote?: boolean
}

/**
 * 主机信息：**这台机器本身**（操作系统 / 架构 / 内核 / CPU / 内存）。
 *
 * 早先这些字段与存储驱动、运行时、插件混在同一张卡里，看上去像是
 * 「Docker 引擎的配置」——其实它们描述的是**宿主操作系统**。
 * 守护进程只是**恰好知道**这些值（`/info` 顺带返回），并不代表它们属于引擎配置：
 * 把「24 核」「62.5 GiB」放在「引擎信息」里，读者的第一反应是「引擎用了多少核」。
 *
 * 缺失字段用占位符而非跳过整行：离线主机这块直接消失、页面只剩四个破折号，
 * 看起来像加载失败。
 */
const hostRows = computed<InfoRow[]>(() => {
  const h = currentHost.value
  if (!h) return []

  const d = h.daemon
  const v = h.version

  // 守护进程给的是完整发行版串（Ubuntu 24.04.5 LTS · 24.04 · linux）；
  // 取不到时退回 /version 的 `os`（linux）。
  const osText = d
    ? [d.operatingSystem, d.osVersion, d.osType].filter(Boolean).join(' · ')
    : ''

  return [
    { label: t('host.info.os'), value: osText || v?.os || MISSING },
    { label: t('host.info.arch'), value: d?.architecture || v?.arch || MISSING },
    { label: t('host.info.kernel'), value: d?.kernelVersion ?? MISSING },
    {
      label: t('host.info.cpus'),
      value: d && d.cpuCount > 0 ? String(d.cpuCount) : MISSING
    },
    {
      label: t('host.info.memory'),
      value: d && d.memoryBytes > 0 ? formatBytes(d.memoryBytes, byteUnits.value) : MISSING
    }
  ]
})

/**
 * 引擎信息：**Docker 引擎自身**的版本与配置。
 *
 * 全部来自 `/version` 与 `/info`，不新增任何 Docker 调用。
 *
 * ⚠️ **CPU / 内存不在这里**：它们是宿主机的静态上限，不是引擎配置
 * （见上方 `hostRows`）。
 *
 * 卡内分四个小组：版本 / 存储与驱动 / 运行时与组件 / 插件。
 * 不平铺成一长串，是因为「Docker 版本 27.3.1」与「网络插件 bridge, host, …」
 * 回答的是完全不同的问题，混排时后者会被挤到最后一行。
 */
const engineGroups = computed<InfoGroup[]>(() => {
  const h = currentHost.value
  if (!h) return []

  const d = h.daemon
  const v = h.version

  // ===== 版本 =====
  const versionRows: InfoRow[] = [
    { label: t('host.engine.version'), value: v?.version ?? MISSING },
    { label: t('host.engine.apiVersion'), value: v?.apiVersion ?? MISSING },
    { label: t('host.engine.goVersion'), value: v?.goVersion ?? MISSING }
  ]
  // 预留：Agent 模式排在 1.0.0 之后，届时后端才会返回 agentVersion
  if (h.agentVersion) {
    versionRows.push({ label: t('host.engine.agentVersion'), value: h.agentVersion })
  }

  // ===== 存储与驱动 =====
  const storageRows: InfoRow[] = []
  if (d?.storageDriver) {
    storageRows.push({
      label: t('host.engine.storage'),
      value:
        d.storageDriverStatus.length > 0
          ? `${d.storageDriver} (${d.storageDriverStatus.map((pair) => pair.join('=')).join(', ')})`
          : d.storageDriver
    })
  }
  if (d?.loggingDriver) storageRows.push({ label: t('host.engine.logging'), value: d.loggingDriver })
  if (d?.cgroupDriver) {
    storageRows.push({
      label: t('host.engine.cgroup'),
      value: d.cgroupVersion ? `${d.cgroupDriver} v${d.cgroupVersion}` : d.cgroupDriver
    })
  }
  if (d?.dockerRootDir) storageRows.push({ label: t('host.engine.rootDir'), value: d.dockerRootDir })

  // ===== 运行时与组件 =====
  const runtimeRows: InfoRow[] = []
  if (d?.defaultRuntime) {
    runtimeRows.push({
      label: t('host.engine.runtime'),
      value:
        d.runtimes.length > 0 ? `${d.defaultRuntime} (${d.runtimes.join(', ')})` : d.defaultRuntime
    })
  }
  if (d && (d.containerdVersion || d.runcVersion)) {
    runtimeRows.push({
      label: t('host.engine.components'),
      value: [
        d.containerdVersion ? `containerd ${d.containerdVersion}` : null,
        d.runcVersion ? `runc ${d.runcVersion}` : null
      ].filter(Boolean).join(' · ')
    })
  }
  if (d && (d.initBinary || d.initVersion)) {
    runtimeRows.push({
      label: t('host.engine.init'),
      value: [d.initBinary ?? null, d.initVersion ?? null].filter(Boolean).join(' ')
    })
  }
  if (d?.firewallBackend) {
    runtimeRows.push({ label: t('host.engine.firewall'), value: d.firewallBackend })
  }
  // 布尔量恒显：「未启用」也是确定的事实，不该被当成「没取到」
  if (d) {
    runtimeRows.push({
      label: t('host.engine.liveRestore'),
      value: d.liveRestoreEnabled ? t('host.engine.enabled') : t('host.engine.disabled')
    })
  }

  // ===== 插件 =====
  // 空列表写「无」而不是「—」——「没装授权插件」是确定的事实，
  // 而「—」在这套界面里表示「没取到」，两者不能混。
  const pluginRows: InfoRow[] = []
  if (d) {
    const pluginGroups: { label: string; list: string[] }[] = [
      { label: t('host.engine.pluginVolume'), list: d.plugins.volume },
      { label: t('host.engine.pluginNetwork'), list: d.plugins.network },
      { label: t('host.engine.pluginLog'), list: d.plugins.log },
      { label: t('host.engine.pluginAuth'), list: d.plugins.authorization }
    ]
    for (const group of pluginGroups) {
      pluginRows.push({
        label: group.label,
        value: group.list.length > 0 ? group.list.join(', ') : t('host.engine.none')
      })
    }
  }

  return [
    { title: t('host.engine.groupVersion'), rows: versionRows },
    { title: t('host.engine.groupStorage'), rows: storageRows },
    { title: t('host.engine.groupRuntime'), rows: runtimeRows },
    { title: t('host.engine.groupPlugins'), rows: pluginRows }
  ].filter((group) => group.rows.length > 0)
})

/**
 * 仓库与安全：**镜像仓库与网络 / 安全配置**。
 *
 * 独立成卡而不是并进引擎信息：这几项是**运维要经常改的配置**
 * （换镜像加速器、加不安全仓库、调代理），混在一堆只读的引擎版本信息里会被淹没。
 *
 * ⚠️ 代理 URL 的 userinfo 已由服务端遮蔽，这里直接展示。
 */
const securityRows = computed<InfoRow[]>(() => {
  const d = currentHost.value?.daemon
  if (!d) return []

  const rows: InfoRow[] = []

  if (d.registryMirrors.length > 0) {
    rows.push({ label: t('host.security.mirrors'), value: d.registryMirrors.join(', ') })
  }

  if (d.insecureRegistries.length > 0) {
    rows.push({
      label: t('host.security.insecureRegistries'),
      value: d.insecureRegistries.join(', ')
    })
  }

  if (d.indexServerAddress) {
    rows.push({ label: t('host.security.indexServer'), value: d.indexServerAddress })
  }

  if (d.httpProxy || d.httpsProxy || d.noProxy) {
    rows.push({
      label: t('host.security.proxy'),
      value: [
        d.httpProxy ? `HTTP ${d.httpProxy}` : null,
        d.httpsProxy ? `HTTPS ${d.httpsProxy}` : null,
        d.noProxy ? `NO_PROXY ${d.noProxy}` : null
      ].filter(Boolean).join(' · ')
    })
  }

  if (d.securityOptions.length > 0) {
    rows.push({
      label: t('host.security.securityOptions'),
      value: d.securityOptions.join(', ')
    })
  }

  if (d.cdiSpecDirs.length > 0) {
    rows.push({ label: t('host.security.cdiDirs'), value: d.cdiSpecDirs.join(', ') })
  }

  return rows
})

/**
 * 「字段尚未同步」的说明只出现在**最后一张**有缺失字段的卡上。
 *
 * 每张卡各写一遍会出现两段一模一样的文字（离线主机：主机信息卡与引擎信息卡
 * 都有占位符），像是重复渲染出了 bug。
 */
const lastPendingKey = computed(() => {
  const pending = infoSections.value.filter((section) => section.pendingNote)
  return pending.length > 0 ? pending[pending.length - 1]!.key : null
})

/** 引擎自报的告警；单独成块，因为它们要的是「被看见」而不是「被查到」 */
const engineWarnings = computed(() => currentHost.value?.daemon?.warnings ?? [])

/**
 * 信息卡：**主机信息** / **引擎信息** / **仓库与安全**，三张。
 *
 * 分界依据是**「这条信息描述的是谁」**：
 * - 主机信息 = 这台机器（操作系统、内核、架构、CPU、内存）
 * - 引擎信息 = Docker 引擎（版本、驱动、运行时、插件）
 * - 仓库与安全 = 引擎与外部世界的连接配置（镜像仓库、代理、安全选项）
 *
 * 早先按**数据来源**分（`/version` vs `/info`），结果宿主操作系统的字段
 * 落进了「引擎信息」——来源相同不等于语义相同。
 *
 * 卡内按**单列**排：每个字段独占一行。两列网格在宽屏下会把「标签」与「值」
 * 拉到两半，读者必须横跨半屏才能把它们配对——技术字段一多，这种扫描成本
 * 立刻变成负担。
 */
const infoSections = computed<InfoSection[]>(() => {
  const sections: InfoSection[] = []

  if (currentHost.value) {
    sections.push({
      key: 'host',
      title: t('host.info.title'),
      description: t('host.info.hint'),
      groups: [{ rows: hostRows.value }],
      pendingNote: hostRows.value.some((r) => r.value === MISSING)
    })
  }

  if (engineGroups.value.length > 0) {
    sections.push({
      key: 'engine',
      title: t('host.engine.title'),
      description: t('host.engine.hint'),
      groups: engineGroups.value,
      pendingNote: engineGroups.value.some((g) => g.rows.some((r) => r.value === MISSING))
    })
  }

  if (securityRows.value.length > 0) {
    sections.push({
      key: 'security',
      title: t('host.security.title'),
      description: t('host.security.hint'),
      groups: [{ rows: securityRows.value }]
    })
  }

  return sections
})
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

    <!--
      信息卡：主机信息 / 引擎信息 / 仓库与安全三张，由 infoSections 驱动
      而不是写三段重复的 `dl`。

      ⚠️ **单列**：每个字段独占一行。两列网格在宽屏下会把「标签」与「值」拉到
      相距半屏的地方，读者得横跨半屏才能把它们配对——技术字段一多，这种扫描
      成本立刻变成负担。

      ⚠️ 值**允许折行**而不是截断：存储驱动、组件版本、镜像加速器这些值本来就长，
      截断等于没给（`overlayfs (driver-type:io.containerd.snapsh…` 就是这么来的）。
    -->
    <SectionCard
      v-for="(section, index) in infoSections"
      :key="section.key"
      :class="index > 0 ? 'app-section' : ''"
      :title="section.title"
      :description="section.description"
      density="dense"
    >
      <div v-for="group in section.groups" :key="group.title ?? '_'" class="mt-4 first:mt-0">
        <!-- 分组标题：同一张卡里区分内容层次（如「仓库与安全」） -->
        <h3 v-if="group.title" class="mb-1 text-xs font-medium text-content-muted">
          {{ group.title }}
        </h3>

        <dl>
          <!--
            标签定宽、值紧随其后（而非 justify-between 右对齐）：
            右对齐会让「标签」到「值」隔出几百像素，眼睛必须横跨整行才能把两者
            关联起来。这里按定义列表的常规形态排布。
          -->
          <div
            v-for="row in group.rows"
            :key="row.label"
            class="flex items-baseline gap-4 py-2.5 border-b border-dashed border-line-subtle last:border-0"
          >
            <dt class="w-24 sm:w-32 shrink-0 text-xs text-content-muted">{{ row.label }}</dt>
            <dd
              class="min-w-0 flex-1 text-[13px] font-mono break-words"
              :class="row.value === MISSING ? 'text-content-faint' : 'text-content-body'"
              :title="row.value === MISSING ? t('host.info.pending') : row.value"
            >
              {{ row.value }}
            </dd>
          </div>
        </dl>
      </div>

      <p
        v-if="section.key === lastPendingKey"
        class="mt-3 flex items-start gap-1.5 text-xs text-content-faint"
      >
        <span class="i-carbon-information shrink-0 mt-px" aria-hidden="true" />
        <span>{{ t('host.info.pending') }}</span>
      </p>
    </SectionCard>

    <!-- 引擎自报的告警：单独成块，样式上区别于普通信息卡 -->
    <div
      v-if="engineWarnings.length > 0"
      class="app-section rounded-lg border border-status-warn/40 bg-status-warn-soft/60 px-4 py-3"
    >
      <p class="flex items-center gap-2 text-sm font-semibold text-status-warn">
        <span class="i-carbon-warning-alt" aria-hidden="true" />
        {{ t('host.engine.warnings') }}
      </p>
      <ul class="mt-2 space-y-1 text-xs text-content-body">
        <li v-for="(warning, i) in engineWarnings" :key="i" class="break-words">{{ warning }}</li>
      </ul>
    </div>
  </div>
</template>