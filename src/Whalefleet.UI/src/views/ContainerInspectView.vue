<script setup lang="ts">
import { computed, defineAsyncComponent, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ContainerStatusBadge from '@/components/business/ContainerStatusBadge.vue'
import ContainerOperationDialogs from '@/components/business/ContainerOperationDialogs.vue'
import {
  inspectContainer,
  pauseContainer,
  removeContainer,
  restartContainer,
  resumeContainer,
  startContainer,
  stopContainer
} from '@/api/container'
import { ApiRequestError } from '@/api'
import { useContainerStats } from '@/composables/useContainerStats'
import { usePreferencesStore } from '@/stores/preferences'
import { formatBytes } from '@/utils/container-status'
import { formatDateTime } from '@/utils/datetime'
import { formatMemory, formatNetwork, formatPercent, memoryPercent } from '@/utils/log-format'
import { canPerform, isActive, actionDisabledReason } from '@/utils/container-actions'
import type { ByteUnits } from '@/utils/container-status'
import type { ContainerDetail } from '@/types/api'

/**
 * 指标图**懒加载**：ECharts 只在容器详情页用到，放进主包会白送给所有用户。
 * `defineAsyncComponent` 让 Vite 把它拆成单独的 chunk。
 */
const MetricsChart = defineAsyncComponent(
  () => import('@/components/business/MetricsChart.vue')
)

/**
 * 终端面板同样**懒加载**（0.8.0）：xterm + FitAddon + WebLinksAddon 约 300 KB，
 * 而绝大多数人打开详情页只是看一眼配置。`defineAsyncComponent` 让 Vite 把它拆成独立 chunk。
 */
const ContainerTerminal = defineAsyncComponent(
  () => import('@/components/business/ContainerTerminal.vue')
)

/**
 * 容器详情页（0.0.11）。
 *
 * ## 为什么是分组卡片而不是一个 JSON 树
 * `docs/features.md` 早期写的是「Inspect 完整配置信息」，实现时改成
 * **基本信息 + 四组结构化卡片**，理由是：JSON 树要求用户自己找字段，
 * 而「这个容器占多少内存、挂在哪个卷上、有没有暴露端口」这些问题
 * 答在结构化视图里只需一眼。为此牺牲的是「一个字段不落」——
 * 未展示的字段不在后端契约里（后端已经是精选投影），所以不存在遗漏。
 *
 * ## 为什么 env 单独警示
 * 详情里的 `config.env` 常含数据库连接串、API Token。后端已在类型注释里写明这一点，
 * 前端再警示一次：这是唯一一个「展示它可能造成泄漏」的区域。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const preferences = usePreferencesStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const containerId = computed(() => String(route.params.id ?? ''))

const detail = ref<ContainerDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/**
 * 正在执行的写操作：让页头按钮转圈并禁用。
 *
 * ⚠️ **不用 `loading`**：那个是「详情还在拉」，而写操作期间详情并没有重新拉取，
 * 用同一个标志会让刷新按钮也转起来，而它转起来的样子与「正在停止容器」毫无关系。
 */
const acting = ref(false)

/** 七个操作对话框的宿主（与列表页共用同一个组件）。 */
const operationDialogs = ref<InstanceType<typeof ContainerOperationDialogs> | null>(null)

/** 当前操作对象（详情页只有一个容器，因此固定是它）。 */
const operationTarget = computed(() =>
  detail.value ? { id: detail.value.id, name: detail.value.name } : null
)

/** 容器是否处于「已暂停」状态。 */
const isPaused = computed(() => detail.value?.state.paused === true)

/** 容器是否处于「活动」一侧（运行中 / 已暂停 / 重启中）。 */
const isRunning = computed(() => (detail.value ? isActive(detail.value.state.status) : false))

/**
 * 页头主按钮的动作。
 *
 * ⚠️ **按状态取**而不是给三个按钮：页头放三个「启动 / 停止 / 继续」会让用户
 * 面对一个永远有一半不可点的按钮组，而不可点的那些没有存在的意义。
 */
type PrimaryAction = 'start' | 'stop' | 'resume'

const primaryAction = computed<PrimaryAction>(() => {
  if (isPaused.value) return 'resume'
  if (isRunning.value) return 'stop'

  return 'start'
})

const primaryLabelKey = computed(() => `container.action.${primaryAction.value}`)

/**
 * 实时指标（0.5.0）。
 *
 * 开关在「设置 → 实时更新 → 容器详情页指标」里；关掉时**根本不建连接**——
 * 那是一条到守护进程的长连接，默认连着总归是不对。
 */
const stats = useContainerStats({
  hostId: () => hostId.value,
  containerId: () => containerId.value,
  enabled: () => preferences.realtimeStats
})

/** 最近一次采样。 */
const sample = stats.sample

/** 内存占用百分比（上限未知时为 null）。 */
const memoryUsagePercent = computed(() =>
  sample.value ? memoryPercent(sample.value.memoryUsage, sample.value.memoryLimit) : null
)

/**
 * 指标图的时间窗（秒）；0 = 全部缓冲（约两分钟）。
 *
 * 只做「从右往左切一段」：服务端环形缓冲只有 60 点，切更早的窗口本就无数据。
 */
const windowSeconds = ref(60)

/** 按时间窗截取的采样（旧 → 新）。 */
const visibleSamples = computed(() => {
  const all = stats.samples.value
  if (windowSeconds.value <= 0 || all.length === 0) return all

  const latest = Date.parse(all[all.length - 1]!.timestamp)
  if (Number.isNaN(latest)) return all

  const floor = latest - windowSeconds.value * 1000

  return all.filter((item) => Date.parse(item.timestamp) >= floor)
})

/** 健康状态 → i18n key（**显式映射**，不在模板里拼 key——拼错只会告警不报错）。 */
const HEALTH_LABEL_KEY = {
  none: 'container.health.none',
  starting: 'container.health.starting',
  healthy: 'container.health.healthy',
  unhealthy: 'container.health.unhealthy'
} as const

const byteUnits = computed<ByteUnits>(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

/** 「字段为空」与「值为空串」要区分：前者显示占位符，后者可能是用户刻意设的空值。 */
function orDash(value: string | null | undefined): string {
  return value && value.length > 0 ? value : '—'
}

async function load(): Promise<void> {
  if (!hostId.value || !containerId.value) return

  loading.value = true
  error.value = null
  try {
    detail.value = await inspectContainer(hostId.value, containerId.value)
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
    detail.value = null
  } finally {
    loading.value = false
  }
}

function backToList(): void {
  void router.push({ name: 'host-containers', params: { hostId: hostId.value } })
}

/**
 * 重启策略的线上写法 → i18n key。
 *
 * 必须显式映射，**不能**用模板字符串拼：Docker 给的是 `unless-stopped` / `on-failure`，
 * 而 i18n key 用的是 `unlessStopped` / `onFailure`。拼出来的 key 不存在，
 * 界面上会直接显示 `container.restartPolicy.unless-stopped` 这种原始 key——
 * 难看，且只有真看到界面才会发现（vue-i18n 找不到 key 时是警告不报错）。
 */
const RESTART_POLICY_KEYS: Record<string, string> = {
  no: 'container.restartPolicy.no',
  always: 'container.restartPolicy.always',
  'on-failure': 'container.restartPolicy.onFailure',
  'unless-stopped': 'container.restartPolicy.unlessStopped'
}

function restartPolicyLabel(policy: string): string {
  const key = RESTART_POLICY_KEYS[policy]

  // 未登记的写法（将来 Docker 新增）直接显示原文，而不是一个 key 路径。
  return key ? t(key) : policy
}

function openLogs(): void {
  void router.push({
    name: 'host-container-logs',
    params: { hostId: hostId.value, id: containerId.value }
  })
}

/**
 * 执行一个写操作，然后刷新详情。
 *
 * ⚠️ **成功也刷新**：状态变化不会推给这个页面（实时通道只推「异常退出」），
 * 而「点了没变」是最容易被当成按钮坏了的现象。
 */
async function runAction(action: () => Promise<void>): Promise<void> {
  acting.value = true
  try {
    await action()
    await load()
  } catch {
    // 错误已由全局拦截器提示
  } finally {
    acting.value = false
  }
}

/** 页头主按钮（启动 / 停止 / 继续）。 */
async function onPrimary(): Promise<void> {
  const id = containerId.value

  await runAction(() => {
    if (primaryAction.value === 'start') return startContainer(hostId.value, id)
    if (primaryAction.value === 'stop') return stopContainer(hostId.value, id)

    return resumeContainer(hostId.value, id)
  })
}

/** 重启容器。 */
async function onRestart(): Promise<void> {
  await runAction(() => restartContainer(hostId.value, containerId.value))
}

/** 暂停容器。 */
async function onPause(): Promise<void> {
  await runAction(() => pauseContainer(hostId.value, containerId.value))
}

/**
 * 终止容器（等价于 `docker kill`）。
 *
 * ⚠️ **单条也走信号对话框**：默认 SIGKILL，但用户可以指定别的信号，
 * 而非终止信号可能不会真的终止容器。
 */
function onKill(): void {
  operationDialogs.value?.openKill([
    { id: containerId.value, name: detail.value?.name ?? containerId.value }
  ])
}

/**
 * 删除容器。
 *
 * 运行中的容器需要 `force`，而 force 会跳过优雅停止——未落盘的数据会丢，
 * 因此提示必须出现在确认框里而不是藏进按钮的 tooltip。
 */
async function onRemove(): Promise<void> {
  const running = isRunning.value

  try {
    await ElMessageBox.confirm(
      t('container.confirm.remove.message', { name: detail.value?.name ?? '' }),
      t('container.confirm.remove.title'),
      {
        confirmButtonText: running ? t('container.confirm.remove.force') : t('common.confirm'),
        cancelButtonText: t('common.cancel'),
        type: running ? 'warning' : 'info'
      }
    )
  } catch {
    return
  }

  await runAction(() => removeContainer(hostId.value, containerId.value, running))
}

/**
 * 重建 / 编辑完成后的处理。
 *
 * ⚠️ **必须换到新容器的 ID 上**：重建后旧容器已让位（改名保留），
 * 而本页的 URL 还指着旧 ID——不换的话刷新一下就是 404，
 * 而用户看到的是「重建完页面坏了」。
 */
async function onRecreated(result: { containerId: string; replacedContainerId: string | null }): Promise<void> {
  await router.replace({
    name: 'host-container-inspect',
    params: { hostId: hostId.value, id: result.containerId }
  })

  await load()
}

/** 对话框里的其它操作完成后刷新详情。 */
async function onOperationDone(): Promise<void> {
  await load()
}

/**
 * NanoCpus 是「十亿分之一核」，展示时必须换算成核数——直接显示 1500000000 没人看得懂。
 *
 * 小于 1 核时保留两位小数（0.25 核这种配额很常见）；大于等于 1 核时用整数 + 一位小数
 * （8 核不必写成「8.00」）。
 */
function cpuQuota(nanoCpus: number | null): string {
  if (nanoCpus == null || nanoCpus === 0) return t('container.detail.unlimited')

  const cores = nanoCpus / 1_000_000_000

  return `${cores >= 1 ? cores.toFixed(1) : cores.toFixed(2)} ${t('container.detail.cores')}`
}

watch(containerId, load, { immediate: true })

// 容器变了 → 重订阅指标；开关变了 → 跟着开关建/断连接。
watch(
  [containerId, () => preferences.realtimeStats],
  () => {
    void stats.stop()
    if (preferences.realtimeStats) {
      void stats.start()
    }
  },
  { immediate: true }
)
</script>

<template>
  <div v-loading="loading">
    <PageHeader
      icon="i-carbon-cube"
      :title="t('container.detail.title')"
      :subtitle="detail ? `${detail.name} · ${detail.image}` : undefined"
      narrow
    >
      <template #actions>
        <el-button @click="backToList">{{ t('container.backToList') }}</el-button>
        <el-button @click="openLogs">{{ t('container.action.logs') }}</el-button>

        <!--
          生命周期操作（0.7.0）：详情页**自成一体**，不用回列表页才能操作容器。

          ⚠️ **主按钮按状态取**（启动 / 停止 / 继续）：页头放三个「启动 / 停止 / 继续」
          会让用户面对一组永远有一半不可点的按钮，而不可点的那些没有存在的意义。

          ⚠️ **已暂停时不显示「重启」**：暂停的进程收不到 SIGTERM，
          点了会先等满超时（默认 10 秒）才 SIGKILL——界面看起来就像卡住了。
        -->
        <el-button
          v-if="detail"
          :loading="acting"
          :disabled="!canPerform(primaryAction, detail.state.status)"
          @click="onPrimary"
        >
          {{ t(primaryLabelKey) }}
        </el-button>

        <el-button
          v-if="detail && !isPaused"
          :loading="acting"
          :disabled="!canPerform('restart', detail.state.status)"
          @click="onRestart"
        >
          {{ t('container.action.restart') }}
        </el-button>

        <!--
          低频与危险操作收进「更多」下拉：终止 / 暂停 / 继续 / 复制 / 重建 / 编辑 / 删除。

          ⚠️ **重建与编辑带警告色**：它们会**替换**容器（ID 变了），
          而下拉菜单里没有二次确认对话框的停留时间，必须一眼看出它不一样。
        -->
        <el-dropdown v-if="detail" trigger="click">
          <el-button :disabled="acting">{{ t('container.action.more') }}</el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item
                :disabled="acting || !canPerform('kill', detail.state.status)"
                @click="onKill"
              >
                <span class="i-carbon-warning" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.kill') }}</span>
              </el-dropdown-item>

              <el-dropdown-item
                :disabled="acting || !canPerform('pause', detail.state.status)"
                @click="onPause"
              >
                <span class="i-carbon-pause" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.pause') }}</span>
              </el-dropdown-item>

              <el-dropdown-item
                :disabled="acting || !canPerform('resume', detail.state.status)"
                @click="onPrimary"
              >
                <span class="i-carbon-play" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.resume') }}</span>
              </el-dropdown-item>

              <el-dropdown-item :disabled="acting" @click="operationDialogs?.openDuplicate()">
                <span class="i-carbon-copy" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.duplicate') }}</span>
              </el-dropdown-item>

              <el-dropdown-item :disabled="acting" @click="operationDialogs?.openRecreate()">
                <span class="i-carbon-renew" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.recreate') }}</span>
                <span class="ml-2 text-xs text-status-warn">{{ t('container.recreate.warning') }}</span>
              </el-dropdown-item>

              <el-dropdown-item :disabled="acting" @click="operationDialogs?.openEdit()">
                <span class="i-carbon-edit" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.edit') }}</span>
                <span class="ml-2 text-xs text-status-warn">{{ t('container.recreate.warning') }}</span>
              </el-dropdown-item>

              <el-dropdown-item
                divided
                :disabled="acting"
                class="text-status-down"
                @click="onRemove"
              >
                <span class="i-carbon-trash-can" aria-hidden="true" />
                <span class="ml-2">{{ t('container.action.remove') }}</span>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>

        <el-button :loading="loading" type="primary" @click="load">
          {{ t('container.action.refresh') }}
        </el-button>
      </template>

      <template v-if="detail" #badge>
        <ContainerStatusBadge :status="detail.state.status" class="ml-2 align-middle" />
      </template>
    </PageHeader>

    <div v-if="error" class="app-section">
      <EmptyState
        icon="i-carbon-warning-alt"
        :title="t('container.inspect.failed')"
        :description="error"
      >
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </EmptyState>
    </div>

    <template v-else-if="detail">
      <div class="flex flex-col gap-4">
        <SectionCard :title="t('container.detail.basic')">
          <dl class="app-kv">
            <div><dt>{{ t('container.id') }}</dt><dd class="font-mono text-xs">{{ detail.id }}</dd></div>
            <div><dt>{{ t('container.col.created') }}</dt><dd>{{ formatDateTime(detail.created) }}</dd></div>
            <div>
              <dt>{{ t('container.detail.startedAt') }}</dt>
              <dd>{{ formatDateTime(detail.state.startedAt) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.detail.finishedAt') }}</dt>
              <dd>{{ formatDateTime(detail.state.finishedAt) }}</dd>
            </div>
            <!-- ⚠️ 运行中时后端刻意把 exitCode 抹成 null（Docker 给的 0 会被读成「已正常退出」），
                 因此这里只在已停止时展示，不能给个 0 糊弄过去。 -->
            <div v-if="!detail.state.running">
              <dt>{{ t('container.detail.exitCode') }}</dt>
              <dd :class="{ 'text-status-down': detail.state.exitCode !== 0 }">
                {{ detail.state.exitCode ?? '—' }}
              </dd>
            </div>
            <div v-if="detail.state.healthStatus">
              <dt>{{ t('container.detail.health') }}</dt>
              <dd
                :class="{
                  'text-status-ok': detail.state.healthStatus === 'healthy',
                  'text-status-down': detail.state.healthStatus === 'unhealthy'
                }"
              >
                {{ t(HEALTH_LABEL_KEY[detail.state.healthStatus]) }}
              </dd>
            </div>
            <!-- 重启计数：反复重启的容器靠这一行才能被看见（退出码为 0 时尤其如此） -->
            <div v-if="detail.state.restartCount > 0">
              <dt>{{ t('container.detail.restartCount') }}</dt>
              <dd class="text-status-warn">{{ detail.state.restartCount }}</dd>
            </div>
            <div v-if="detail.state.oomKilled">
              <dt>{{ t('container.detail.oomKilled') }}</dt>
              <dd class="text-status-down">{{ t('container.detail.yes') }}</dd>
            </div>
          </dl>
        </SectionCard>

        <SectionCard
          v-if="detail.state.running"
          :title="t('container.detail.metrics')"
          :description="t('container.detail.metricsHint')"
        >
          <div v-if="sample" class="flex flex-col gap-3">
            <div class="flex items-center justify-between gap-4">
              <span class="text-sm text-content-muted">{{ t('container.detail.cpu') }}</span>
              <span class="font-mono text-sm">{{ formatPercent(sample.cpuPercent) }}</span>
            </div>

            <div class="flex flex-col gap-1">
              <div class="flex items-center justify-between gap-4">
                <span class="text-sm text-content-muted">{{ t('container.detail.memory') }}</span>
                <span class="font-mono text-sm">
                  {{ formatMemory(sample.memoryUsage, sample.memoryLimit) }}
                </span>
              </div>
              <!-- ⚠️ 上限未知（守护进程没设限）时不画进度条：一条空条会被读成「没占内存」。 -->
              <el-progress
                v-if="memoryUsagePercent !== null"
                :percentage="memoryUsagePercent"
                :stroke-width="6"
                :show-text="false"
              />
            </div>

            <div class="flex items-center justify-between gap-4">
              <span class="text-sm text-content-muted">{{ t('container.detail.networkIo') }}</span>
              <span class="font-mono text-sm">
                {{ formatNetwork(sample.networkRxBytes, sample.networkTxBytes) }}
              </span>
            </div>

            <!--
              多指标叠加图（0.6.0）：CPU / 内存走左轴（百分比），网络速率走右轴（字节/秒）。
              量纲不同不能共用一个轴——否则网络曲线会把 CPU 压成一条直线。
            -->
            <div class="mt-2">
              <div class="mb-1.5 flex items-center justify-between gap-3">
                <span class="text-xs text-content-muted">{{ t('container.detail.metricsChart') }}</span>
                <el-radio-group v-model="windowSeconds" size="small">
                  <el-radio-button :value="60">1m</el-radio-button>
                  <el-radio-button :value="120">2m</el-radio-button>
                  <el-radio-button :value="0">{{ t('container.detail.all') }}</el-radio-button>
                </el-radio-group>
              </div>

              <MetricsChart :samples="visibleSamples" />
            </div>
          </div>

          <p v-else class="text-sm text-content-faint">
            {{ stats.status.value === 'error' ? (stats.error.value ?? '—') : t('container.detail.metricsWaiting') }}
          </p>
        </SectionCard>

        <SectionCard :title="t('container.detail.hostConfig')">
          <dl class="app-kv">
            <div>
              <dt>{{ t('container.create.restartPolicy') }}</dt>
              <dd>{{ restartPolicyLabel(detail.hostConfig.restartPolicy) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.autoRemove') }}</dt>
              <dd>{{ detail.hostConfig.autoRemove ? t('container.detail.yes') : t('container.detail.no') }}</dd>
            </div>
            <div><dt>{{ t('container.create.network') }}</dt><dd>{{ orDash(detail.hostConfig.networkMode) }}</dd></div>
            <div>
              <dt>{{ t('container.detail.memoryLimit') }}</dt>
              <dd>{{ formatBytes(detail.hostConfig.memoryLimitBytes, byteUnits) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.detail.nanoCpus') }}</dt>
              <dd>{{ cpuQuota(detail.hostConfig.nanoCpus) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.detail.privileged') }}</dt>
              <dd>{{ detail.hostConfig.privileged ? t('container.detail.yes') : t('container.detail.no') }}</dd>
            </div>
          </dl>
        </SectionCard>

        <SectionCard :title="t('container.detail.config')">
          <dl class="app-kv">
            <div>
              <dt>{{ t('container.create.cmd') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.cmd.join(' ')) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.entrypoint') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.entrypoint.join(' ')) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.workingDir') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.workingDir) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.hostname') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.hostname) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.tty') }}</dt>
              <dd>{{ detail.config.tty ? t('container.detail.yes') : t('container.detail.no') }}</dd>
            </div>
          </dl>
        </SectionCard>

        <SectionCard :title="t('container.detail.mounts')">
          <div v-if="detail.mounts.length > 0" class="app-table-wrap">
            <table class="app-table">
              <thead>
                <tr>
                  <th>{{ t('container.detail.mountSource') }}</th>
                  <th>{{ t('container.detail.mountTarget') }}</th>
                  <th class="text-right">{{ t('container.detail.mountMode') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(mount, index) in detail.mounts" :key="index">
                  <td class="font-mono text-xs">{{ mount.source || '—' }}</td>
                  <td class="font-mono text-xs">{{ mount.destination }}</td>
                  <td class="text-right text-xs">
                    {{ mount.readWrite ? t('container.detail.readWrite') : t('container.detail.readOnly') }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p v-else class="text-sm text-content-faint">—</p>
        </SectionCard>

        <SectionCard :title="t('container.detail.networks')">
          <div v-if="detail.networks.length > 0" class="app-table-wrap">
            <table class="app-table">
              <thead>
                <tr>
                  <th>{{ t('nav.networks') }}</th>
                  <th>{{ t('container.detail.networkIp') }}</th>
                  <th>{{ t('container.detail.networkGateway') }}</th>
                  <th>{{ t('container.detail.networkMac') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="network in detail.networks" :key="network.name">
                  <td>{{ network.name }}</td>
                  <td class="font-mono text-xs">{{ orDash(network.ipAddress) }}</td>
                  <td class="font-mono text-xs">{{ orDash(network.gateway) }}</td>
                  <td class="font-mono text-xs">{{ orDash(network.macAddress) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <p v-else class="text-sm text-content-faint">—</p>
        </SectionCard>

        <SectionCard :title="t('container.detail.env')" :description="t('container.detail.envHint')">
          <ul v-if="detail.config.env.length > 0" class="app-log-list">
            <li v-for="(item, index) in detail.config.env" :key="index" class="font-mono text-xs">
              {{ item }}
            </li>
          </ul>
          <p v-else class="text-sm text-content-faint">—</p>
        </SectionCard>

        <SectionCard v-if="Object.keys(detail.config.labels).length > 0" :title="t('container.detail.labels')">
          <dl class="app-kv">
            <div v-for="(value, key) in detail.config.labels" :key="key">
              <dt class="font-mono">{{ key }}</dt>
              <dd class="font-mono text-xs break-all">{{ value }}</dd>
            </div>
          </dl>
        </SectionCard>

        <!--
          交互式终端（0.8.0）：面板本身**懒加载**——xterm 加两个 addon 约 300 KB，
          只有真的点开终端的人需要它。
        -->
        <SectionCard :title="t('container.terminal.title')" :description="t('container.terminal.hint')">
          <ContainerTerminal
            :host-id="hostId"
            :container-id="detail.id"
            :disabled="!canPerform('terminal', detail.state.status)"
            :disabled-reason="actionDisabledReason('terminal', detail.state.status)"
          />
        </SectionCard>
      </div>
    </template>

    <!--
      七个操作对话框的**统一宿主**（0.7.0），与列表页共用同一个组件。

      ⚠️ **重建后要换到新容器 ID 上**：旧容器已让位，而本页的 URL 还指着它——
      不换的话用户看到的会是「重建完页面 404 了」。
    -->
    <ContainerOperationDialogs
      :host-id="hostId"
      :target="operationTarget"
      @done="onOperationDone"
      @recreated="onRecreated"
    />
  </div>
</template>