<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatCard from '@/components/common/StatCard.vue'
import StatusBadge from '@/components/common/StatusBadge.vue'
import CopyableText from '@/components/common/CopyableText.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import ListPagination from '@/components/common/ListPagination.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import ContainerListTable from '@/components/business/ContainerListTable.vue'
import ComposeApplyDialog from '@/components/business/ComposeApplyDialog.vue'
import ComposeWebhookCard from '@/components/business/ComposeWebhookCard.vue'
import { useComposeTask } from '@/composables/useComposeTask'
import {
  inspectComposeProject,
  removeComposeProject,
  stopComposeProject,
  updateComposeProject
} from '@/api/compose'
import {
  getContainerHealth,
  listContainers,
  restartContainer,
  resumeContainer,
  startContainer,
  stopContainer
} from '@/api/container'
import { ApiRequestError } from '@/api'
import { composeStatusKey, composeTone } from '@/utils/compose-display'
import { canPerform, isActive } from '@/utils/container-actions'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import { resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import type {
  ComposeProjectDetail,
  ContainerHealthSummary,
  ContainerSummary
} from '@/types/api'

/**
 * Compose 项目详情页（0.10.0）。
 *
 * ## 主体是**容器列表**，不是「服务表」
 *
 * 早先这里有一张自己造的「服务表」（服务名 / 镜像 / 状态 / 健康 / 端口 / 重启策略），
 * 长得和容器列表完全不一样：状态是裸文本而不是徽标、端口是一行拼接而不是可点开的链接、
 * 也没有健康与重启次数。
 * 同一个东西在两个页面显示成两种样子，用户没法把「服务」与「容器」对上。
 *
 * 现在这里直接复用**容器列表页的那张表**（`ContainerListTable`），
 * 按 compose 项目过滤出这个项目的容器：
 * - 状态、健康、重启次数、端口链接、镜像、创建时间——**与容器列表页完全一致**；
 * - 多一列「服务」，把容器归到服务名下（容器叫 `shop-api-1`，服务叫 `api`）；
 * - 行内动作与容器列表页一致，另加「终端」。
 *
 * ⚠️ **不给「删除容器」**：compose 会把被删的容器重新建出来，
 * 那个按钮点下去只是让用户白等一次重建。页面已有「删除编排」，那才是对应的动作。
 *
 * ## 「哪个服务没起来」靠 `missingServices` 回答
 *
 * 换成容器列表后会丢掉一件事：**没有容器的服务在列表里根本不出现**。
 * 因此后端额外给出 `missingServices`（声明了但还没有容器的服务名），
 * 页面上方单独提示——否则「服务少了」这件事就彻底看不见了。
 *
 * ## 状态自己算，不轮询详情接口
 *
 * 轮询的是**容器列表端点**（按项目过滤），而不是 `inspect`：
 * 后者的响应里带着 Compose YAML 原文（上限 256 KB），5 秒一次太重。
 * 整体状态由前端按与后端同口径的规则推导（见 `composeStatus`）。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const detail = ref<ComposeProjectDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/** 应用对话框。 */
const applyVisible = ref(false)

const { task, busy: taskBusy, percent, step, run } = useComposeTask()

/** URL 上的 hostId。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

/** URL 上的 Compose 项目名称。 */
const composeName = computed(() => String(route.params.name ?? ''))

/** 是否受管（本平台部署的）。 */
const managed = computed(() => detail.value?.summary.managed === true)

/* ==================================================================
 * 容器列表（按 compose 项目过滤）
 * ================================================================== */

/** 轮询间隔（毫秒）。与容器列表页一致。 */
const POLL_INTERVAL = 5000

/** 健康刷新间隔（毫秒）。与容器列表页一致（健康是批量 inspect，不必太密）。 */
const HEALTH_REFRESH_INTERVAL = 10_000

const containers = ref<ContainerSummary[]>([])
const containerTotal = ref(0)
const containerPageIndex = ref(0)
const pageSize = ref<number>(usePreferencesStore().pageSize)

/** 状态筛选。默认「全部」——**停了的服务必须看得见**。 */
const stateFilter = ref<'all' | 'running' | 'stopped'>('all')

/** 容器健康信息（容器 ID → 健康与退出原因）。 */
const healthMap = ref<Record<string, ContainerHealthSummary>>({})

/** 正在执行操作的容器 ID 集合。 */
const busyIds = ref<Set<string>>(new Set())

let pollTimer: ReturnType<typeof setInterval> | null = null
let healthTimer: ReturnType<typeof setInterval> | null = null

/**
 * 容器列表查询参数。
 *
 * ⚠️ **项目过滤在服务端**：分页也是服务端做的，
 * 前端过滤只能筛到当前页（页脚说 100 个、翻过去只剩 3 个）。
 */
function containerParams() {
  return {
    // ⚠️ 「全部」必须换算成真实条数再发出去：0 会被后端
    // `Math.Clamp(pageSize, 1, 100)` 夹成 1，用户选了「全部」却只看到一行。
    project: composeName.value,
    state: stateFilter.value,
    pageIndex: containerPageIndex.value,
    pageSize: resolvePageSize(pageSize.value, containerTotal.value)
  }
}

/** 列出容器（按项目过滤）。 */
async function loadContainers(): Promise<void> {
  if (!hostId.value || !composeName.value) return

  try {
    const result = await listContainers(hostId.value, containerParams())

    containers.value = result.items
    containerTotal.value = result.total
    containerPageIndex.value = result.pageIndex
  } catch {
    // 单次失败不打断轮询：保留旧数据，下一轮再试。
    // ⚠️ **不写进 `error`**——容器列表是辅助内容，
    // 一次网络抖动不该把整页换成错误态。
  }
}

/** 一次性拉当前页容器的健康信息。失败时保持旧值（降级为「运行中」）。 */
async function refreshHealth(): Promise<void> {
  if (!hostId.value) return

  const ids = containers.value.map((container) => container.id)
  if (ids.length === 0) return

  try {
    const result = await getContainerHealth(hostId.value, ids)
    const next: Record<string, ContainerHealthSummary> = {}

    for (const item of result.items) next[item.containerId] = item

    healthMap.value = next
  } catch {
    // 单次失败不打扰用户：健康信息的语义就是「有就给、没有就退回运行中」
  }
}

function startPolling(): void {
  stopPolling()
  pollTimer = setInterval(() => {
    if (!document.hidden) void loadContainers()
  }, POLL_INTERVAL)
}

function stopPolling(): void {
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

function startHealthPolling(): void {
  stopHealthPolling()
  void refreshHealth()

  healthTimer = setInterval(() => {
    if (!document.hidden) void refreshHealth()
  }, HEALTH_REFRESH_INTERVAL)
}

function stopHealthPolling(): void {
  if (healthTimer !== null) {
    clearInterval(healthTimer)
    healthTimer = null
  }
}

/**
 * 整体状态（由容器列表推导）。
 *
 * ⚠️ **口径必须与后端 `ResolveStatus` 一致**：
 * 0 个容器 → unknown；全在跑 → running；一个都没跑 → stopped；其余 → partial。
 * 两套规则不一致的症状是「徽标说运行中、下面却有个停着的容器」。
 */
const composeStatus = computed<'running' | 'partial' | 'stopped' | 'unknown'>(() => {
  if (containers.value.length === 0) return 'unknown'

  const running = containers.value.filter((container) => container.state === 'running').length

  if (running === containers.value.length) return 'running'
  if (running === 0) return 'stopped'

  return 'partial'
})

/** 当前是否带着状态筛选（用于区分「本来就没有」与「筛掉了」）。 */
const hasContainerFilter = computed(() => stateFilter.value !== 'all')

/* ==================================================================
 * 详情加载
 * ================================================================== */

async function load(): Promise<void> {
  if (!hostId.value || !composeName.value) return

  loading.value = true
  try {
    detail.value = await inspectComposeProject(hostId.value, composeName.value)
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : t('compose.loadFailed')
  } finally {
    loading.value = false
  }
}

/**
 * 对话框提交（更新）。
 *
 * @param payload 提交内容。
 */
async function onApply(payload: {
  name: string
  composeContent: string
  pullAlways: boolean
  pruneOrphans: boolean
}): Promise<void> {
  const snapshot = await run(
    hostId.value,
    (taskId) =>
      updateComposeProject(hostId.value, {
        name: payload.name,
        composeContent: payload.composeContent,
        taskId,
        pullAlways: payload.pullAlways,
        pruneOrphans: payload.pruneOrphans
      }),
    load
  )

  if (!snapshot) {
    ElMessage.error(t('compose.task.failed'))
    return
  }

  if (snapshot.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('compose.task.failed'))
    return
  }

  ElMessage.success(t('compose.task.succeeded'))
}

/** 停止编排。 */
async function stopComposeProjectByName(): Promise<void> {
  const snapshot = await run(
    hostId.value,
    (taskId) => stopComposeProject(hostId.value, { name: composeName.value, taskId }),
    load
  )

  if (snapshot?.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('compose.task.failed'))
    return
  }

  if (snapshot) {
    ElMessage.success(t('compose.task.succeeded'))
  }
}

/** 删除编排。 */
async function removeComposeProjectByName(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('compose.remove.confirm', { name: composeName.value }),
      t('compose.remove.title'),
      {
        type: 'warning',
        confirmButtonText: t('common.confirm'),
        cancelButtonText: t('common.cancel')
      }
    )
  } catch {
    return
  }

  const snapshot = await run(
    hostId.value,
    (taskId) =>
      removeComposeProject(hostId.value, {
        name: composeName.value,
        taskId,
        removeVolumes: false
      }),
    load
  )

  if (snapshot?.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('compose.task.failed'))
    return
  }

  if (snapshot) {
    ElMessage.success(t('compose.task.succeeded'))
    // 删掉了就回列表：留在详情页只会看到一个空壳
    void router.push({ name: 'host-composes', params: { hostId: hostId.value } })
  }
}

/** 回列表。 */
function backToList(): void {
  void router.push({ name: 'host-composes', params: { hostId: hostId.value } })
}

/* ==================================================================
 * 容器行内动作
 * ================================================================== */

/** 容器是否处于「活动」一侧（可被停止 / 重启）。 */
function isActiveContainer(container: ContainerSummary): boolean {
  return isActive(container.state)
}

/** 容器是否处于「已暂停」状态。 */
function isPaused(container: ContainerSummary): boolean {
  return container.state === 'paused'
}

/** 进容器详情。 */
function openInspect(container: ContainerSummary): void {
  void router.push({
    name: 'host-container-inspect',
    params: { hostId: hostId.value, id: container.id }
  })
}

/** 看容器日志。 */
function openLogs(container: ContainerSummary): void {
  void router.push({
    name: 'host-container-logs',
    params: { hostId: hostId.value, id: container.id }
  })
}

/** 进容器终端。 */
function openTerminal(container: ContainerSummary): void {
  void router.push({
    name: 'host-container-terminal',
    params: { hostId: hostId.value, id: container.id }
  })
}

/** 标记某个容器「正在操作」。 */
function markBusy(containerId: string, busy: boolean): void {
  const next = new Set(busyIds.value)
  if (busy) next.add(containerId)
  else next.delete(containerId)
  busyIds.value = next
}

/**
 * 执行一次容器变更，然后立刻刷新列表。
 *
 * 刻意**不等轮询周期**：用户点完「启动」就该看到状态变了，等最多 5 秒会像没生效。
 */
async function runContainerAction(
  container: ContainerSummary,
  action: () => Promise<void>
): Promise<void> {
  markBusy(container.id, true)

  try {
    await action()
    ElMessage.success(t('container.action.done', { name: container.name }))
  } catch (e) {
    ElMessage.error(e instanceof ApiRequestError ? e.message : String(e))
  } finally {
    await loadContainers()
    markBusy(container.id, false)
  }
}

/** 切换状态筛选。 */
async function onStateFilterChange(next: 'all' | 'running' | 'stopped'): Promise<void> {
  stateFilter.value = next
  // ⚠️ **必须回到第 0 页**：筛完还停在第 3 页，结果一定是空的。
  containerPageIndex.value = 0
  await loadContainers()
}

/** 总页数。 */
const containerPageCount = computed(() =>
  Math.max(
    1,
    Math.ceil(containerTotal.value / resolvePageSize(pageSize.value, containerTotal.value))
  )
)

/** 翻页。 */
async function goToPage(next: number): Promise<void> {
  if (next < 0 || next >= containerPageCount.value) return

  containerPageIndex.value = next
  await loadContainers()
}

/**
 * 换每页条数。
 *
 * ⚠️ 这里**只更新本地状态、不发请求**：真正的查询统一由 `goToPage(0)` 触发。
 */
function onPageSizeChange(size: number): void {
  usePreferencesStore().setPageSize(size)
  pageSize.value = size
}

/** 手动刷新（工具条上的刷新按钮）。 */
async function refresh(): Promise<void> {
  await Promise.all([loadContainers(), refreshHealth()])
}

/**
 * 行内动作按钮。
 *
 * ⚠️ **可用性判定复用 `canPerform`**：判定逻辑散到各页面，
 * 迟早会漏掉一个状态——而漏掉的后果是「给出了一个点了会报错的按钮」。
 */
interface RowAction {
  key: string
  icon: string
  label: string
  disabled?: boolean
  hint?: string
  run: (container: ContainerSummary) => void
}

function rowActions(container: ContainerSummary): RowAction[] {
  const busy = busyIds.value.has(container.id) || !container.effectivePermissions.includes('container.write')
  const active = isActiveContainer(container)
  const paused = isPaused(container)

  return [
    {
      key: 'detail',
      icon: 'i-carbon-document-view',
      label: t('container.action.detail'),
      run: openInspect
    },
    {
      // ⚠️ **已暂停时主按钮是「继续」而不是「停止」**：暂停中的进程收不到 SIGTERM，
      // 点「停止」会先等满超时（默认 10 秒）才 SIGKILL——界面看起来就像卡住了。
      // 而「继续」是那个状态下唯一即时生效的动作。
      key: 'power',
      icon: paused ? 'i-carbon-play' : active ? 'i-carbon-stop' : 'i-carbon-play',
      label: t(
        paused
          ? 'container.action.resume'
          : active
            ? 'container.action.stop'
            : 'container.action.start'
      ),
      disabled: busy,
      run: (c) =>
        runContainerAction(c, () =>
          paused
            ? resumeContainer(hostId.value, c.id)
            : active
              ? stopContainer(hostId.value, c.id)
              : startContainer(hostId.value, c.id)
        )
    },
    {
      key: 'restart',
      icon: 'i-carbon-restart',
      label: t('container.action.restart'),
      disabled: busy || !canPerform('restart', container.state),
      hint: canPerform('restart', container.state) ? undefined : t('container.action.disabled.restart'),
      run: (c) => runContainerAction(c, () => restartContainer(hostId.value, c.id))
    },
    {
      key: 'logs',
      icon: 'i-carbon-terminal',
      label: t('container.action.logs'),
      run: openLogs
    },
    {
      key: 'terminal',
      icon: 'i-carbon-data-view-alt',
      label: t('container.action.terminal'),
      // ⚠️ **终端要求容器在跑**：exec 会挂在启动那一刻，
      // 已暂停的容器虽然 `Running` 仍为 true，终端却会「空白、打字没反应」。
      disabled: busy || !canPerform('terminal', container.state),
      hint: canPerform('terminal', container.state)
        ? undefined
        : t('container.action.disabled.terminal'),
      run: openTerminal
    }
  ]
}

watch(
  () => [route.params.hostId, route.params.name],
  () => {
    stopPolling()
    stopHealthPolling()
    containerPageIndex.value = 0
    error.value = null
    // 换项目等于换了一批完全不同的行：不清健康缓存的话，
    // 新项目的容器会显示上一个项目的健康信息
    healthMap.value = {}

    void load().then(() => {
      void loadContainers()
      startPolling()
      startHealthPolling()
    })
  },
  { immediate: true }
)

onBeforeUnmount(() => {
  stopPolling()
  stopHealthPolling()
})
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-layers"
      :title="t('compose.detail.title', { name: composeName })"
      :title-tip="t('page.composes.subtitle')"
      narrow
    >
      <!--
        状态徽标与「外部项目」标签挂在标题后面。
        ⚠️ 「外部项目」必须显式标出来：它意味着「这个项目不是本平台部署的，
        没有 YAML，不能更新」——只隐藏「更新」按钮会让用户以为功能坏了。
      -->
      <template #badge>
        <StatusBadge :tone="composeTone(composeStatus)" class="ml-2">
          {{ t(composeStatusKey(composeStatus)) }}
        </StatusBadge>
        <el-tag v-if="!managed" size="small" type="info" class="ml-2">
          {{ t('compose.managed.external') }}
        </el-tag>
      </template>

      <template #actions>
        <el-button @click="backToList">{{ t('common.back') }}</el-button>
        <!--
          ⚠️ **「更新」只对受管项目开放**：外部 compose 项目（用户自己在命令行
          `docker compose up` 的）没有 YAML，更新必然失败（后端报 1903）。
          因此入口直接不给，而不是等点了才报错。
        -->
        <el-button v-if="managed" type="primary" @click="applyVisible = true">
          {{ t('compose.action.update') }}
        </el-button>
        <el-button :disabled="taskBusy" @click="stopComposeProjectByName">
          {{ t('compose.action.stop') }}
        </el-button>
        <el-button type="danger" plain :disabled="taskBusy" @click="removeComposeProjectByName">
          {{ t('compose.action.remove') }}
        </el-button>
      </template>
    </PageHeader>

    <div v-if="taskBusy" class="mb-4">
      <el-alert type="info" :closable="false" show-icon>
        <template #title>{{ t('compose.task.running') }}</template>
        <div class="mt-1 flex items-center gap-3">
          <el-progress
            :percentage="percent ?? 0"
            :status="task?.status === 'failed' ? 'exception' : undefined"
            class="flex-1"
          />
          <span class="text-xs text-content-muted">{{ step ?? '' }}</span>
        </div>
      </el-alert>
    </div>

    <div v-if="error" class="p-6 flex flex-col items-center gap-3">
      <p class="text-sm text-content-muted">{{ error }}</p>
      <el-button @click="load">{{ t('common.retry') }}</el-button>
    </div>

    <template v-else-if="detail">
      <div class="flex flex-col gap-4">
        <!-- 外部项目提示：说明「为什么没有更新按钮」 -->
        <el-alert v-if="!managed" type="info" :closable="false" show-icon>
          <template #title>{{ t('compose.managed.hint') }}</template>
        </el-alert>

        <!-- 概览 -->
        <SectionCard :title="t('compose.section.overview')">
          <div class="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <StatCard
              icon="i-carbon-cube"
              :value="detail.summary.serviceCount"
              :label="t('compose.column.services')"
            />
            <StatCard
              icon="i-carbon-activity"
              :value="`${detail.summary.runningCount} / ${detail.summary.containerCount}`"
              :label="t('compose.column.containers')"
            />
            <StatCard
              icon="i-carbon-network-3"
              :value="detail.summary.networkCount"
              :label="t('compose.column.networks')"
            />
            <StatCard
              icon="i-carbon-data-volume"
              :value="detail.summary.volumeCount"
              :label="t('compose.column.volumes')"
            />
          </div>

          <!--
            缺失服务提示：**换掉服务表之后，这是「哪个服务没起来」的唯一答案**——
            没有容器的服务在容器列表里根本不出现。
            ⚠️ 只在真的有缺失时显示：没有 YAML 的外部项目不适用。
          -->
          <el-alert
            v-if="detail.missingServices.length > 0"
            class="mt-4"
            type="warning"
            :closable="false"
            show-icon
          >
            <template #title>{{ t('compose.missing.title') }}</template>
            <p class="mt-1 text-xs">
              {{ t('compose.missing.hint', { services: detail.missingServices.join('、') }) }}
            </p>
          </el-alert>

          <!-- 解析期的提示必须显眼：它是「有些配置没生效」的唯一解释 -->
          <el-alert
            v-if="detail.warnings.length > 0"
            class="mt-4"
            type="warning"
            :closable="false"
            show-icon
          >
            <template #title>{{ t('compose.warnings.title') }}</template>
            <ul class="mt-1 list-disc pl-4 text-xs">
              <li v-for="(warning, index) in detail.warnings" :key="index">{{ warning }}</li>
            </ul>
          </el-alert>
        </SectionCard>

        <!--
          容器列表：**复用容器列表页的那张表**（`ContainerListTable`），
          按 compose 项目过滤。
          ⚠️ 不提供「删除容器」：compose 会把被删的容器重建出来。
        -->
        <SectionCard :title="t('compose.section.containers')" density="flush">
          <template #actions>
            <ListToolbar>
              <template #filters>
                <el-select
                  :model-value="stateFilter"
                  class="!w-32"
                  @change="onStateFilterChange"
                >
                  <el-option value="all" :label="t('container.filter.all')" />
                  <el-option value="running" :label="t('container.filter.running')" />
                  <el-option value="stopped" :label="t('container.filter.stopped')" />
                </el-select>

                <span class="text-xs text-content-muted">
                  {{ t('compose.total', { total: containerTotal }) }}
                </span>
              </template>

              <template #extra>
                <!--
                  刷新按钮**用图标**：它是一个「随时可点」的高频动作，
                  而「刷新」两个字占了整个按钮的宽度却不增加任何信息量。
                  ⚠️ 图标放在 `icon` 插槽里（不是默认插槽）：EP 的 Button 在 `loading` 时
                  会用转圈图标**替换** `icon` 插槽的内容，两个都渲染就成了「转圈 + 图标」。
                -->
                <el-tooltip
                  :content="t('common.refresh')"
                  placement="bottom"
                  :show-after="TIP_DELAY_MS"
                >
                  <span class="inline-flex">
                    <el-button text circle :aria-label="t('common.refresh')" @click="refresh">
                      <template #icon><span class="i-carbon-renew" /></template>
                    </el-button>
                  </span>
                </el-tooltip>
              </template>
            </ListToolbar>
          </template>

          <ContainerListTable
            :containers="containers"
            :health-map="healthMap"
            show-service-column
            :selectable="false"
            @open-detail="openInspect"
          >
            <template #row-actions="{ row }">
              <IconActionButton
                v-for="action in rowActions(row)"
                :key="action.key"
                :icon="action.icon"
                :label="action.label"
                :disabled="action.disabled"
                :hint="action.hint"
                @click="action.run(row)"
              />
            </template>
          </ContainerListTable>

          <!-- 空态分两种：本来就没有容器 / 筛掉了 -->
          <EmptyState
            v-if="containers.length === 0"
            :icon="hasContainerFilter ? 'i-carbon-search' : 'i-carbon-cube'"
            :title="
              hasContainerFilter ? t('container.empty.filtered') : t('compose.containers.empty')
            "
            :description="
              hasContainerFilter
                ? t('container.empty.filteredHint')
                : t('compose.containers.emptyHint')
            "
          />

          <ListPagination
            :total="containerTotal"
            :page-index="containerPageIndex"
            :page-size="pageSize"
            @update:page-index="goToPage"
            @update:page-size="onPageSizeChange"
          />
        </SectionCard>

        <!-- Webhook -->
        <SectionCard :title="t('compose.section.webhook')">
          <ComposeWebhookCard
            :host-id="hostId"
            :compose="detail.summary"
            @changed="load"
          />
        </SectionCard>

        <!-- YAML 原文 -->
        <SectionCard :title="t('compose.section.compose')">
          <template #actions>
            <!-- 复制按钮：原文只能看不能复制时，用户要手工框选一大段文本 -->
            <CopyableText v-if="detail.composeContent" :value="detail.composeContent" />
          </template>

          <template v-if="detail.composeContent">
            <pre
              class="max-h-[32rem] overflow-auto rounded bg-surface-hover p-3 font-mono text-xs text-content"
            >{{ detail.composeContent }}</pre>
          </template>
          <EmptyState
            v-else
            icon="i-carbon-document"
            :title="t('compose.compose.unavailable')"
            :description="t('compose.compose.unavailableHint')"
          />
        </SectionCard>
      </div>
    </template>

    <ComposeApplyDialog
      v-model:visible="applyVisible"
      mode="update"
      :compose="detail?.summary ?? null"
      :compose-content="detail?.composeContent ?? null"
      @submit="onApply"
    />
  </div>
</template>
