<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { TableInstance } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import ListPagination from '@/components/common/ListPagination.vue'
import BatchActionBar from '@/components/common/BatchActionBar.vue'
import CreateContainerDialog from '@/components/business/CreateContainerDialog.vue'
import ContainerRenameDialog from '@/components/business/ContainerRenameDialog.vue'
import ContainerUpdateDialog from '@/components/business/ContainerUpdateDialog.vue'
import ContainerDuplicateDialog from '@/components/business/ContainerDuplicateDialog.vue'
import ContainerAddVolumesDialog from '@/components/business/ContainerAddVolumesDialog.vue'
import ContainerStatusBadge from '@/components/business/ContainerStatusBadge.vue'
import { onActionRequest } from '@/composables/useActionBus'
import {
  batchRemoveContainers,
  getContainerHealth,
  listContainers,
  removeContainer,
  restartContainer,
  startContainer,
  stopContainer,
  type ContainerSortField
} from '@/api/container'
import { ApiRequestError } from '@/api'
import { containerTone, formatPort } from '@/utils/container-status'
import { formatDateTime } from '@/utils/datetime'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import { formatMemory, formatPercent } from '@/utils/log-format'
import { useContainerStatsBoard } from '@/composables/useContainerStatsBoard'
import { subscribeContainerDied } from '@/realtime/notification'
import { PAGE_SIZE_ALL, resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import type {
  ContainerHealthSummary,
  ContainerStatsSample,
  ContainerSummary
} from '@/types/api'

/**
 * 容器列表页（0.0.11）。
 *
 * ## 为什么自己管数据而不用通用请求 composable
 * 页面要同时处理三件互相牵连的事：**轮询**（状态会变）、**操作后立即刷新**（启动后不该等下一轮）、
 * **主机切换时重置**。把它们塞进一个通用 composable，刷新时机就变成「谁最后调用谁生效」，
 * 那段代码比这三行显式逻辑难读得多。
 *
 * ## 轮询的四个决定
 * - **5 秒**：容器状态变化以「人操作后看结果」为主，5 秒足够跟手，又不至于把 API 打满。
 * - **页面隐藏时跳过**：`document.hidden` 时不发请求。后台标签页每 5 秒发一次毫无意义。
 * - **失败不打断轮询**：单次抖动只静默忽略，继续下一轮；连续失败才升级为错误态。
 * - **切换主机立即重取**：`watch(hostId)` 里先 `stopPolling()` 再 `load()`，
 *   避免把 A 主机的数据画在 B 主机的页面上——那是最容易引发误操作的一类 bug
 *   （对着错误的主机点删除）。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

/** 轮询间隔（毫秒） */
const POLL_INTERVAL = 5000

const containers = ref<ContainerSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
/**
 * 每页条数来自用户偏好（设置页可改），`PAGE_SIZE_ALL`（0）表示「全部」。
 *
 * 类型是 `number` 而不是 store 的 `PageSize` 联合：后端回显的 `result.pageSize`
 * 是任意 number，写回时需要接住它。「全部」靠与 `PAGE_SIZE_ALL` 相等来判断，不靠类型。
 */
const pageSize = ref<number>(usePreferencesStore().pageSize)

/** 偏好 store（实时更新开关）。 */
const prefs = usePreferencesStore()
const includeStopped = ref(true)
const loading = ref(false)
/** 错误信息；非 null 时显示错误态 + 重试，而不是保留一份可能是陈旧的数据 */
const error = ref<string | null>(null)
/** 正在执行操作的容器 id 集合：让行内按钮各自转圈，而不是整页转圈 */
const busyIds = ref<Set<string>>(new Set())
const createDialogVisible = ref(false)

/**
 * 0.3.0 的四个对话框共用一组「当前操作对象」状态。
 *
 * ⚠️ 不用四个独立的 visible + id，而是共用一个 `target`：
 * 四个对话框分属四组状态时，会出现「A 对话框开着，B 的 id 却指到了另一个容器」的组合——
 * 而那种状态下用户点确认，操作会作用在**错的容器**上。
 * 一个 `target` 从构造上排除了这种组合。
 */
const operationTarget = ref<ContainerSummary | null>(null)
const renameVisible = ref(false)
const updateVisible = ref(false)
const duplicateVisible = ref(false)
const addVolumesVisible = ref(false)

const lastSyncedAt = ref<number | null>(null)

/** 当前排序列；`null` = 不排序，后端保持守护进程返回顺序（创建时间降序） */
const sortField = ref<ContainerSortField | null>(null)
const sortOrder = ref<'asc' | 'desc'>('desc')

/** 批量操作选中的容器；`el-table` 的 selection 是受控的，得自己留一份才能算数量 */
const selected = ref<ContainerSummary[]>([])
const batchBusy = ref(false)

/**
 * 表格实例：只有 `clearSelection()` 用得到，但它是实例方法，
 * 模板 ref 是唯一拿得到它的方式（`BatchActionBar` 本身不该知道 el-table 的存在）。
 */
const tableRef = ref<TableInstance | null>(null)

// 命令面板 / 快捷键的「创建容器」经动作总线转到这里（对话框开关仍归本页所有）
onActionRequest('createContainer', () => {
  createDialogVisible.value = true
})

let pollTimer: ReturnType<typeof setInterval> | null = null

/** URL 上的 hostId。**以它为准**而不是 store.currentId：深链进入时 store 可能还没被守卫写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

/**
 * 总页数。
 *
 * 必须用 `resolvePageSize` 而不是直接 `total / pageSize`：
 * `pageSize` 可能是 `PAGE_SIZE_ALL`（0，除以它得 Infinity），
 * 也可能是被上限夹紧后的值。与分页组件的口径不同步会出现
 * 「el-pagination 显示了第 2 页按钮，但 goToPage 的边界检查拦下它」——按钮点不动。
 */
const pageCount = computed(() =>
  Math.max(1, Math.ceil(total.value / resolvePageSize(pageSize.value, total.value)))
)

/**
 * 列表查询参数（`load` 与 `poll` 共用）。
 *
 * 抽出来是因为这两处一旦各写一遍，排序参数就会只在其中一处漏掉——
 * 而症状是「刚点完排序是对的，5 秒后自己跳回原顺序」，极难自查。
 */
function listParams() {
  return {
    all: includeStopped.value,
    pageIndex: pageIndex.value,
    // ⚠️ 「全部」必须在这里换算成真实条数再发出去：0 传出去会被后端
    // Math.Clamp(pageSize, 1, 100) 夹成 1，用户选了"全部"却只看到一行。
    // 换算规则（含上限夹紧）收在 resolvePageSize 里，与分页组件的口径共用一份。
    pageSize: resolvePageSize(pageSize.value, total.value),
    ...(sortField.value ? { sortBy: sortField.value, sortDir: sortOrder.value } : {})
  }
}

/**
 * 容器是否处于「活动」一侧（可被停止 / 重启）。
 *
 * ⚠️ 判据不能只是 `state === 'running'`：**已暂停的容器在 Docker 语义里仍是运行中**，
 * 对它显示「启动」既不符合事实（它没停），点下去也只会得到一次「目标状态已达成」的
 * 空操作——用户会以为按钮失灵。过渡态（paused / restarting / removing）全部归到「活动」一侧，
 * 因此这里复用色调映射，而不是再列一次状态名（列两次早晚会漏一个）。
 */
function isActive(container: ContainerSummary): boolean {
  const tone = containerTone(container.state)

  return tone === 'ok' || tone === 'warn'
}

async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true
  try {
    const result = await listContainers(hostId.value, listParams())

    containers.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100，
    // 拿请求里的值去算「还有没有下一页」会得出错误结论。
    pageIndex.value = result.pageIndex
    // 「全部」不回写：回显的是换算后的真实条数（如 57），写回去就把「全部」降级成了「57 条/页」，
    // 之后新增到 60 个容器就再也看不到全部了——而用户当初要的就是"全都列出来"。
    if (pageSize.value !== PAGE_SIZE_ALL) pageSize.value = result.pageSize
    error.value = null
    lastSyncedAt.value = Date.now()

    // 0.6.0：列表变了就要重拉健康信息，并重新对齐指标订阅集合
    void refreshHealth()
    void syncBoard()
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

/** 轮询一轮；失败时保留已有数据（单次抖动不该把整页换成错误态）。 */
async function poll(): Promise<void> {
  if (error.value !== null || !hostId.value) return

  try {
    const result = await listContainers(hostId.value, listParams())

    containers.value = result.items
    total.value = result.total
    lastSyncedAt.value = Date.now()

    void syncBoard()
  } catch {
    // 静默：下一轮还会试。这里不写 error，否则一次网络抖动就变成整页错误态。
  }
}

function startPolling(): void {
  stopPolling()
  pollTimer = setInterval(() => {
    if (!document.hidden) void poll()
  }, POLL_INTERVAL)
}

function stopPolling(): void {
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

function markBusy(containerId: string, busy: boolean): void {
  const next = new Set(busyIds.value)
  if (busy) next.add(containerId)
  else next.delete(containerId)
  busyIds.value = next
}

/* ==================================================================
 * 0.6.0：健康（并入状态列） + 指标列
 * ==================================================================
 *
 * 两者成本模型完全不同，因此分开管理：
 * - 健康是**一次批量 inspect**（10 秒一轮）——容器列表接口不返回结构化健康字段；
 * - 指标列是**持续订阅**（每个容器一条到守护进程的流），默认全关，开一列就多一份成本。
 */

/** 健康信息（容器 ID → 健康与退出原因）；查不到的容器不出现（状态退回「运行中」）。 */
const healthMap = ref<Record<string, ContainerHealthSummary>>({})

/** 健康刷新间隔（毫秒）。不与 5 秒列表轮询同频：那是 N 次 inspect，太密无意义。 */
const HEALTH_REFRESH_INTERVAL = 10_000

let healthTimer: ReturnType<typeof setInterval> | null = null

const columns = computed(() => prefs.containerColumns)
const metricsEnabled = computed(
  () => columns.value.cpu || columns.value.memory || columns.value.network
)

/** 一次性拉当前页的健康信息。失败时保持旧值（降级为「运行中」而不是报错）。 */
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
 * 指标订阅板：**一条连接**订阅当前页的全部运行中容器。
 *
 * 只订「运行中」的容器：已停止的容器 stats 流会立刻结束，反复订阅只会刷日志。
 */
const board = useContainerStatsBoard({
  hostId: () => hostId.value,
  enabled: () => metricsEnabled.value
})

const series = board.series

async function syncBoard(): Promise<void> {
  const ids = metricsEnabled.value
    ? containers.value.filter((container) => container.state === 'running').map((c) => c.id)
    : []

  await board.sync(ids)
}

/** 某容器的最新采样（没有则为 null）。 */
function latestSample(containerId: string): ContainerStatsSample | null {
  const points = series[containerId]

  return points && points.length > 0 ? (points[points.length - 1] ?? null) : null
}

/**
 * 把采样序列画成 SVG polyline 的 `points` 属性。
 *
 * 手写 SVG 而不是给每一行建一个 ECharts 实例：一页 20 行 × 3 列 = 60 个图表实例，
 * canvas 的内存与初始化成本完全不成比例。
 */
function sparkline(containerId: string, pick: (sample: ContainerStatsSample) => number | null): string {
  const points = series[containerId]
  if (!points || points.length < 2) return ''

  const values = points.map(pick)
  const numeric = values.filter((value): value is number => value !== null)

  if (numeric.length < 2) return ''

  const max = Math.max(...numeric, 1)
  const width = 80
  const height = 18
  const step = width / (values.length - 1)

  return values
    .map((value, index) => {
      const y = height - ((value ?? 0) / max) * height

      return `${(index * step).toFixed(1)},${y.toFixed(1)}`
    })
    .join(' ')
}

/**
 * 网络速率的展示。
 *
 * ⚠️ 不能用 `formatPercent`：速率的单位是字节/秒，百分比格式化会把它错写成 `x%`。
 */
function formatRate(bytesPerSecond: number | null): string {
  if (bytesPerSecond === null) return '—'

  if (bytesPerSecond >= 1024 * 1024) return `${(bytesPerSecond / 1024 / 1024).toFixed(1)} MB/s`
  if (bytesPerSecond >= 1024) return `${(bytesPerSecond / 1024).toFixed(0)} KB/s`

  return `${Math.round(bytesPerSecond)} B/s`
}

/**
 * 执行一次容器变更，然后立刻刷新列表。
 *
 * 刻意**不等轮询周期**：用户点完「启动」就该看到状态变了，等最多 5 秒会像没生效。
 * 失败时也要 `load()`——否则行内按钮会停在「已启动」的样子，而实际根本没启动。
 */
async function runAction(container: ContainerSummary, action: () => Promise<void>): Promise<void> {
  markBusy(container.id, true)
  try {
    await action()
    ElMessage({ type: 'success', message: t('container.action.done', { name: container.name }) })
  } finally {
    await load()
    markBusy(container.id, false)
  }
}

async function onStart(container: ContainerSummary): Promise<void> {
  await runAction(container, () => startContainer(hostId.value, container.id))
}

async function onStop(container: ContainerSummary): Promise<void> {
  await runAction(container, () => stopContainer(hostId.value, container.id))
}

async function onRestart(container: ContainerSummary): Promise<void> {
  await runAction(container, () => restartContainer(hostId.value, container.id))
}

/**
 * 删除容器。
 *
 * 运行中的容器需要 `force`，而 force 会跳过优雅停止——容器内未落盘的数据会丢，
 * 这是不可逆的。因此默认**不**勾选强制，提示必须出现在确认框里而不是藏进按钮 tooltip。
 */
async function onRemove(container: ContainerSummary): Promise<void> {
  const running = container.state === 'running'

  try {
    // 用 VNode 而不是带 `\n` 的字符串：EP 的 message 走 `innerText` 渲染，
    // `\n` 不会变成换行——两段提示会挤成一行，而「正在运行、需强制」这句恰恰最不能被忽略。
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('container.confirm.remove.message', { name: container.name })),
        running
          ? h('span', { class: 'text-status-warn text-xs' }, t('container.confirm.remove.running'))
          : null
      ]),
      t('container.confirm.remove.title'),
      {
        confirmButtonText: running ? t('container.confirm.remove.force') : t('common.confirm'),
        cancelButtonText: t('common.cancel'),
        type: running ? 'warning' : 'info'
      }
    )
  } catch {
    // 用户取消：不是错误，安静退出。
    return
  }

  await runAction(container, () => removeContainer(hostId.value, container.id, running))
}

async function goToPage(next: number): Promise<void> {
  if (next < 0 || next >= pageCount.value) return

  pageIndex.value = next
  await load()
}

/**
 * 换每页条数。
 *
 * ⚠️ 这里**只更新本地状态、不发请求**：ListPagination 在改页大小时会同时抛
 * `update:pageSize` 与 `update:pageIndex`(0)，两个事件各接一个处理器。
 * 若这里也 load()，一次改页大小就会打两遍请求——而轮询期间的两份响应会互相覆盖，
 * 表现为列表闪一下才停。真正的查询统一由 `goToPage(0)` 触发。
 * 理由见 ListPagination 的 onSizeChange。
 */
function onPageSizeChange(size: number): void {
  usePreferencesStore().setPageSize(size)
  pageSize.value = size
}

/**
 * 表头排序。
 *
 * ⚠️ 必须回到第 0 页：按「大小」排序后，原来第 1 页的内容整体挪到了第 5 页，
 * 停在原页码只会看到一片无关的行，而用户会以为排序没生效。
 *
 * `prop` 传的是**列的 prop**，这里靠一张表映射回后端的 `sortBy`。
 * 直接把 prop 写成 `sortBy` 的值会引入"展示字段名"与"接口字段名"两套词汇，
 * 改接口字段名时就得同步改模板字符串——不如显式列出来。
 */
const SORT_PROPS: Record<string, ContainerSortField> = {
  name: 'name',
  image: 'image',
  state: 'state',
  created: 'created'
}

async function onSortChange({ prop, order }: { prop: string | null; order: string | null }): Promise<void> {
  // `order` 为 null 表示用户点了第三次表头、取消了排序
  const field = prop ? SORT_PROPS[prop] : undefined

  sortField.value = order && field ? field : null
  sortOrder.value = order === 'ascending' ? 'asc' : 'desc'
  pageIndex.value = 0
  await load()
}

function onSelectionChange(rows: ContainerSummary[]): void {
  selected.value = rows
}

function clearSelection(table: { clearSelection: () => void } | null): void {
  table?.clearSelection()
  selected.value = []
}

/**
 * 批量动作：逐个执行并**汇总**结果，而不是"第一个失败就中断"。
 *
 * 中断的坏处很实际：选 10 个容器删 8 个失败 2 个，用户看到的是"删了 0 个"，
 * 却不知道那 8 个其实已经删掉了——重试会再删一遍（已删的报 1401）。
 * 逐个 `catch` 后汇总，报错时明确列出失败项。
 *
 * 启停与重启仍是前端逐条调用：它们**幂等**，批量端点只做了删除
 * （幂等操作发一次请求省不了几个往返，而删除是不可逆的，必须留出逐条汇报失败的空间）。
 */
async function batchRun(
  label: string,
  action: (container: ContainerSummary) => Promise<void>
): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  batchBusy.value = true
  const failed: string[] = []

  try {
    // 并发执行：容器数受页大小限制（≤100），且这些操作互不依赖，
    // 串行会让"删 100 个"变成 100 次往返。Promise.allSettled 而不是 all——
    // 一个 reject 会让整个 all 提前抛，剩下的请求还在飞，结果就说不清了。
    await Promise.allSettled(
      targets.map(async (container) => {
        try {
          await action(container)
        } catch {
          failed.push(container.name)
        }
      })
    )

    const done = targets.length - failed.length
    if (failed.length === 0) {
      ElMessage({ type: 'success', message: t('container.batch.done', { action: label, count: done }) })
    } else {
      ElMessage({
        type: 'warning',
        message: t('container.batch.partial', { action: label, done, failed: failed.length, names: failed.join('、') }),
        showClose: true
      })
    }
  } finally {
    await load()
    batchBusy.value = false
  }
}

async function batchStart(): Promise<void> {
  await batchRun(t('container.action.start'), (c) => startContainer(hostId.value, c.id))
}

async function batchStop(): Promise<void> {
  await batchRun(t('container.action.stop'), (c) => stopContainer(hostId.value, c.id))
}

async function batchRestart(): Promise<void> {
  await batchRun(t('container.action.restart'), (c) => restartContainer(hostId.value, c.id))
}

/**
 * 批量删除（走服务端 `containers/batch-remove`，0.1.0）。
 *
 * 与单个删除同一条原则——不可逆，必须确认；但确认框里要说明两件单条版没说的事：
 *  1. **有多少个正在运行**（force 会跳过优雅停止，数据会丢）
 *  2. **一次性删 N 个**，让人有机会发现勾选范围不对（批量操作最常见的事故）
 * 不给"取消勾选强制"——N 个容器逐个确认强删，交互成本高到会让人闭眼点确认。
 *
 * 失败是**逐条汇报**的：服务端返回的 `failures` 里每一条都带容器 ID 与原因
 * （例如运行中未 force 的 `1404`），界面把它们列出来而不是报一个笼统的「部分失败」。
 */
async function batchRemove(): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  const running = targets.filter((c) => c.state === 'running')

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('container.batch.confirm.message', { count: targets.length })),
        running.length > 0
          ? h('span', { class: 'text-status-warn text-xs' }, t('container.batch.confirm.running', { count: running.length }))
          : null
      ]),
      t('container.batch.confirm.title'),
      {
        confirmButtonText: t('container.batch.confirm.submit'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  batchBusy.value = true

  try {
    const result = await batchRemoveContainers(
      hostId.value,
      targets.map((c) => c.id),
      // 确认框已经说明了强删后果，这里才带 force
      running.length > 0
    )

    if (result.failures.length > 0) {
      // 用容器名而不是 ID 报错：ID 前 12 位对用户没有意义
      const names = result.failures
        .map((failure) => targets.find((c) => c.id === failure.resourceId)?.name ?? failure.resourceId)

      ElMessage({
        type: 'warning',
        message: t('container.batch.partial', {
          action: t('container.action.remove'),
          done: result.succeeded,
          failed: result.failures.length,
          names: names.join('、')
        }),
        showClose: true
      })
    } else {
      ElMessage({
        type: 'success',
        message: t('container.batch.done', { action: t('container.action.remove'), count: result.succeeded })
      })
    }
  } catch (e) {
    // 只有入参非法（1000）与主机不可达（1301/1302）会走到这里，
    // 它们都是**整批**失败，因此报通用错误即可：没有任何一个容器被删。
    ElMessage({ type: 'error', message: e instanceof Error ? e.message : String(e), showClose: true })
  } finally {
    await load()
    batchBusy.value = false
  }
}

async function toggleIncludeStopped(): Promise<void> {
  includeStopped.value = !includeStopped.value
  // 筛选变了还留在第 3 页，结果一定是空的——回到第一页。
  pageIndex.value = 0
  await load()
}

async function onCreated(): Promise<void> {
  createDialogVisible.value = false
  pageIndex.value = 0
  await load()
}

function openLogs(container: ContainerSummary): void {
  void router.push({
    name: 'host-container-logs',
    params: { hostId: hostId.value, id: container.id }
  })
}

function openInspect(container: ContainerSummary): void {
  void router.push({
    name: 'host-container-inspect',
    params: { hostId: hostId.value, id: container.id }
  })
}

/** 行内动作按钮的一行 */
interface RowAction {
  key: string
  /** Carbon 图标类名（`i-carbon-*`） */
  icon: string
  /** tooltip 文案，同时作为无障碍标签 */
  label: string
  /** 仅删除用危险色，其余走默认色 */
  tone?: 'danger'
  /** 正在执行：主操作转圈，其余置灰 */
  loading?: boolean
  disabled?: boolean
  run: (container: ContainerSummary) => void
}

/**
 * 某一行当前可用的动作。
 *
 * ## 排列顺序：详情 → 日常操作 → 危险操作
 * 这不是随手排的，扫视一行时最先看到的按钮应当是**最常点且最无害**的那个：
 *  - 「详情」排最前：它无副作用，却是最高频入口（看一眼配置就回来）。
 *    放在末尾等于把最常用的按钮藏在最远端。
 *  - 「删除」排最后：破坏性操作与其它动作隔开，不会在横扫按钮时误触，
 *    也符合「危险操作远离主流程」的通行做法。
 * 中间三个（启停 / 重启 / 日志）按日常动线排：先改状态，再看输出。
 *
 * 按钮本身的 tooltip / 禁用态包裹 / 无障碍标签都在 `IconActionButton` 里，这里只负责
 * 「有哪些动作、什么图标、什么文案、点了干什么」。列成表而不是写死 5 段模板，是因为
 * 启停要按状态换图标与文案，再乘以其它动作，逐段展开就得在模板里散落着判断；
 * 列成表后一眼能看全，以后加动作（复制 ID / 交互式终端）只加一行。
 *
 * ⚠️ 新加图标必须同步写进 `uno.config.ts` 的 safelist（那里会校验图标在 Carbon 中真实存在）。
 */
function rowActions(container: ContainerSummary): RowAction[] {
  const busy = busyIds.value.has(container.id)
  const active = isActive(container)

  return [
    {
      key: 'detail',
      icon: 'i-carbon-document-view',
      label: t('container.action.detail'),
      run: openInspect
    },
    {
      key: 'power',
      icon: active ? 'i-carbon-stop' : 'i-carbon-play',
      label: t(active ? 'container.action.stop' : 'container.action.start'),
      loading: busy,
      run: active ? onStop : onStart
    },
    {
      // 用 restart 而非 renew：renew 是双箭头**对向**环绕，是「刷新 / 同步」的标准符号，
      // 与页面右上角的刷新按钮（也是 renew）撞成同一个形状；restart 的两枚箭头同向，
      // 才读得出「从头再跑一遍」。
      key: 'restart',
      icon: 'i-carbon-restart',
      label: t('container.action.restart'),
      disabled: busy,
      run: onRestart
    },
    {
      key: 'logs',
      icon: 'i-carbon-terminal',
      label: t('container.action.logs'),
      run: openLogs
    },
    {
      key: 'remove',
      icon: 'i-carbon-trash-can',
      label: t('container.action.remove'),
      tone: 'danger',
      disabled: busy,
      run: onRemove
    }
  ]
}

/**
 * 0.3.0 新增的四个操作（重命名 / 热更新 / 复制 / 追加挂载）。
 *
 * ## 为什么收进「更多」下拉而不是继续平铺图标按钮
 * 平铺下去一行就是 9 个 24px 图标（当前 136px 的操作列要撑到 220px 以上），
 * 而这四个都是**低频**操作——「改了配置之后才会想用」。
 * 平铺的代价是：最常用的「启动 / 停止」被挤到离鼠标更远的位置，
 * 且扫视时难区分哪个是可逆的、哪个会重建容器。
 * 收进下拉后，平铺区只留高频与危险操作，而「会重建容器」的那一项
 * 也能在菜单里单独写上警告图标与副标题。
 *
 * ⚠️ 下拉项的图标也必须同步 safelist（与平铺区同一个坑）。
 */
interface MoreAction {
  key: string
  icon: string
  label: string
  /** 副标题：用于把「会重建容器」这类后果直接写在菜单里 */
  hint?: string
  tone?: 'default' | 'warning'
  disabled?: boolean
  run: (container: ContainerSummary) => void
}

function moreActions(container: ContainerSummary): MoreAction[] {
  const busy = busyIds.value.has(container.id)

  return [
    {
      key: 'rename',
      icon: 'i-carbon-text-align-left',
      label: t('container.action.rename'),
      disabled: busy,
      run: (c) => openOperation(renameVisible, c)
    },
    {
      key: 'update',
      icon: 'i-carbon-settings-adjust',
      label: t('container.action.update'),
      disabled: busy,
      run: (c) => openOperation(updateVisible, c)
    },
    {
      key: 'duplicate',
      icon: 'i-carbon-copy',
      label: t('container.action.duplicate'),
      disabled: busy,
      run: (c) => openOperation(duplicateVisible, c)
    },
    {
      key: 'add-volumes',
      icon: 'i-carbon-add-alt',
      label: t('container.action.addVolumes'),
      // ⚠️ 危险色 + 副标题：它是本组里唯一会**替换**容器的操作，
      // 而菜单里没有二次确认对话框的停留时间，必须一眼看出它不一样。
      hint: t('container.addVolumes.warning'),
      tone: 'warning',
      disabled: busy,
      run: (c) => openOperation(addVolumesVisible, c)
    }
  ]
}

/** 打开某个操作对话框，并把它绑定到 `container`。 */
function openOperation(dialog: Ref<boolean>, container: ContainerSummary): void {
  operationTarget.value = container
  dialog.value = true
}

/** 对话框关闭后清空操作对象：否则下次打开时可能拿到上一行的容器。 */
function closeOperation(dialog: Ref<boolean>): void {
  dialog.value = false
  operationTarget.value = null
}

/** 对话框里操作成功后刷新列表（各操作的影响面不同，统一走整页刷新最省心）。 */
async function onOperationDone(): Promise<void> {
  await load()
}

watch(
  hostId,
  (id) => {
    if (!id) return

    stopPolling()
    pageIndex.value = 0
    error.value = null
    // 换主机等于换了一批完全不同的行：不清选择的话，批量删除会作用到上一台主机的容器上
    selected.value = []
    // 健康信息与指标缓存同样属于「上一台主机」，一并清掉
    healthMap.value = {}
    void board.stop()
    void load().then(() => {
      startPolling()
      startHealthPolling()
    })
  },
  { immediate: true }
)

// 指标列开关变化时重解订阅：关掉最后一列就该把所有流退掉
watch(metricsEnabled, () => {
  void syncBoard()
})

onMounted(() => {
  // 从后台切回来时立刻补一轮：否则用户会盯着暂停前那一份数据，而空白可能长达几分钟。
  const onVisible = () => {
    if (!document.hidden) void poll()
  }
  document.addEventListener('visibilitychange', onVisible)
  onBeforeUnmount(() => document.removeEventListener('visibilitychange', onVisible))
})

onBeforeUnmount(stopPolling)
onBeforeUnmount(stopHealthPolling)

/**
 * 事件驱动的刷新（0.5.0）。
 *
 * ## 为什么还需要轮询
 * 服务端推送的是**容器异常退出**（`die` / `oom`）——「容器停了」这种**正常**
 * 的退出不在事件流里（那属于业务动作，页面自己知道）。
 * 所以轮询不能取消，它仍是「状态变化」的唯一可靠来源；
 * 事件只是把「出事」的那一类**提前**告诉用户。
 *
 * ## 为什么去抖 500ms
 * 守护进程可能在几秒内报出一批容器退出（一次批量重启就会）。
 * 每次事件都刷一次列表等于把页面抽成闪烁的——去抖后只刷最后那一次。
 */
const REFRESH_DEBOUNCE = 500

/** 去抖定时器。 */
let refreshTimer: ReturnType<typeof setTimeout> | null = null

/** 退订函数。 */
let unsubscribeDied: (() => void) | null = null

/**
 * 去抖刷新。
 */
function scheduleRefresh(): void {
  if (refreshTimer !== null) clearTimeout(refreshTimer)

  refreshTimer = setTimeout(() => {
    refreshTimer = null
    void poll()
  }, REFRESH_DEBOUNCE)
}

/**
 * 处理容器退出事件。
 *
 * @param event 事件。
 */
function onContainerDied(event: { hostId: string }): void {
  // 只关心当前主机的事件：全局通知流里包含其它主机的容器。
  if (event.hostId !== hostId.value) return

  scheduleRefresh()
}

/** 订阅（或退订）容器退出事件。 */
function syncEventSubscription(): void {
  unsubscribeDied?.()
  unsubscribeDied = null

  if (prefs.realtimeContainers) {
    unsubscribeDied = subscribeContainerDied(onContainerDied)
  }
}

watch(() => prefs.realtimeContainers, syncEventSubscription)

onMounted(syncEventSubscription)

onBeforeUnmount(() => {
  unsubscribeDied?.()
  unsubscribeDied = null

  if (refreshTimer !== null) {
    clearTimeout(refreshTimer)
    refreshTimer = null
  }
})
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-cube"
      :title="t('nav.containers')"
      :title-tip="t('page.containers.subtitle')"
      narrow
    >
      <template #actions>
        <el-switch
          :model-value="includeStopped"
          :active-text="t('container.filter.all')"
          @change="toggleIncludeStopped"
        />
        <el-button :loading="loading" @click="load">
          {{ t('container.action.refresh') }}
        </el-button>
        <el-button type="primary" @click="createDialogVisible = true">
          {{ t('container.action.create') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span v-if="lastSyncedAt" class="text-xs text-content-muted">
          {{ t('state.updatedAt', { time: formatDateTime(new Date(lastSyncedAt).toISOString()) }) }}
        </span>

        <!--
          显示列（0.6.0）：三个指标列。
          ⚠️ 指标列默认全关：每开一列，每个可见容器就多占一条到守护进程的流。
          ⚠️ 健康**不在这里**：它已并入「状态」列（见 ContainerStatusBadge），
          再给一个独立开关等于让人以为「关掉就不看健康了」——实际上关不掉。

          压成图标放在表头右侧：它是个「偶尔用一次」的低频开关，
          文字按钮会让它看起来跟刷新 / 新建是同一量级的东西。

          ⚠️ **tooltip 必须挂在按钮内部的图标上，不能套在按钮外面**：
          `el-popover` 的 reference 插槽只认**一个元素**（EP 的 `OnlyChild` 把插槽里的
          第一个节点 `cloneVNode` 出来，再把触发器的事件处理器合并上去）。往里塞一个
          `el-tooltip` **组件**时，合并上去的 `onClick` 落到了组件上而不是 DOM 节点上，
          组件又不会把它转发下去——popover 的点击触发器因此**永远绑不上**，
          症状就是「点按钮毫无反应」（tooltip 正常，菜单不弹）。
          所以：外层 reference 是**按钮本身**，tooltip 挂在按钮**里面的图标**上。

          ⚠️ tooltip 放**上方**（`placement="top"`）：菜单面板从下方展开，
          tooltip 若也在下方，浮层会盖在面板的角上并**吃掉落在那儿的点击**。
        -->
        <el-popover placement="bottom-end" :width="200" trigger="click">
          <template #reference>
            <el-button text class="!px-1.5" :aria-label="t('container.columns.title')">
              <el-tooltip
                :content="t('container.columns.title')"
                placement="top"
                :show-after="TIP_DELAY_MS"
              >
                <span class="i-carbon-column text-base" />
              </el-tooltip>
            </el-button>
          </template>

          <div class="flex flex-col gap-2">
            <el-checkbox
              :model-value="columns.cpu"
              @change="prefs.setContainerColumn('cpu', $event as boolean)"
            >
              {{ t('container.col.cpu') }}
            </el-checkbox>
            <el-checkbox
              :model-value="columns.memory"
              @change="prefs.setContainerColumn('memory', $event as boolean)"
            >
              {{ t('container.col.memory') }}
            </el-checkbox>
            <el-checkbox
              :model-value="columns.network"
              @change="prefs.setContainerColumn('network', $event as boolean)"
            >
              {{ t('container.col.network') }}
            </el-checkbox>
          </div>

          <!-- 代价说明放在面板里：图标按钮的 tooltip 只有一行，说不下「为什么默认关闭」 -->
          <p class="mt-2 border-t border-line-subtle pt-2 text-xs text-content-faint">
            {{ t('container.columns.hint') }}
          </p>
        </el-popover>
      </template>

      <!-- 错误态给整块内容位，而不是只弹一个 toast：
           容器页是「要看清单」的场景，弹完就没了的提示等于没说。 -->
      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <template v-else>
        <BatchActionBar :count="selected.length" :busy="batchBusy" @clear="clearSelection(tableRef)">
          <template #default>
            <el-button size="small" :disabled="batchBusy" @click="batchStart">
              {{ t('container.action.start') }}
            </el-button>
            <el-button size="small" :disabled="batchBusy" @click="batchStop">
              {{ t('container.action.stop') }}
            </el-button>
            <el-button size="small" :disabled="batchBusy" @click="batchRestart">
              {{ t('container.action.restart') }}
            </el-button>
            <el-button size="small" type="danger" plain :disabled="batchBusy" @click="batchRemove">
              {{ t('container.action.remove') }}
            </el-button>
          </template>
        </BatchActionBar>

        <!-- 列宽分工：`min-width` 的列吃掉容器富余出来的宽度，`width` 的列纹丝不动。
             状态（徽章 + 一行状态文案）、创建时间（一段定长日期）、操作（一排定宽图标）
             都是内容长度可预期的列，给固定宽才不会随窗口缩放抖动；
             名称 / 镜像 / 端口三列内容长度不定，让它们按比例瓜分多余空间。

             `sortable="custom"` 表示**本地不算、交给后端排**：Docker 侧要一次取全量才能定序，
             前端排当前页会得出一个看着对、全局错的顺序。见 onSortChange 的 SORT_PROPS。 -->
        <el-table
          ref="tableRef"
          v-loading="loading"
          :data="containers"
          :row-key="(row: ContainerSummary) => row.id"
          empty-text=""
          @selection-change="onSelectionChange"
          @sort-change="onSortChange"
        >
          <el-table-column type="selection" width="44" reserve-selection />

          <el-table-column prop="name" :label="t('container.col.name')" min-width="240" sortable="custom">
            <template #default="{ row }">
              <div class="min-w-0">
                <!--
                  名称即详情入口：用 `<button>` 而不是 `<div @click>`。
                  div 版本键盘 Tab 不到、读屏念不出「这是个可点的东西」，
                  也无法响应 Enter / Space——而「点名称看详情」是最顺手的路径，
                  不该只对鼠标用户成立。
                  `text-left` + `w-fit`：默认 button 是居中的且撑满，直接套样式会把
                  两行内容（名称 + 短 ID）居中排掉，左对齐的表格列会被撑歪。

                  ⚠️ 悬浮提示**始终**给，不按「是否被截断」来判断：
                  容器名往往带前缀与随机后缀，同一列里有的长有的短，
                  用户没法从「这行没提示」推断出「这行没被截断」——
                  提示在不在与内容长短无关，才能被当作可靠的入口。
                -->
                <el-tooltip :content="row.name" placement="top-start" :show-after="TIP_DELAY_MS">
                  <button
                    type="button"
                    class="app-truncate block w-fit max-w-full text-left font-medium text-content-strong hover:text-brand-500 hover:underline"
                    @click="openInspect(row)"
                  >
                    {{ row.name }}
                  </button>
                </el-tooltip>
                <el-tooltip :content="row.id" placement="top-start" :show-after="TIP_DELAY_MS">
                  <div class="app-truncate font-mono text-xs text-content-faint">
                    {{ row.id.slice(0, 12) }}
                  </div>
                </el-tooltip>
              </div>
            </template>
          </el-table-column>

          <el-table-column prop="image" :label="t('container.col.image')" min-width="200" sortable="custom">
            <template #default="{ row }">
              <!-- 镜像名同样始终给提示：长镜像名（带仓库地址 + 标签 + 摘要）会被截断 -->
              <el-tooltip :content="row.image" placement="top-start" :show-after="TIP_DELAY_MS">
                <span class="app-truncate block text-sm">{{ row.image }}</span>
              </el-tooltip>
            </template>
          </el-table-column>

          <!--
            状态列：**状态与健康合成一列**。
            「运行中」和「健康」回答的是同一个问题——「这个服务还好吗」，
            分成两列时一行里会出现「运行中 / 不健康」两个词，读者得自己判断哪个才是结论。
            合成后只有四种结论：健康 / 不健康（黄）/ 运行中 / 已停止。
          -->
          <el-table-column prop="state" :label="t('container.col.state')" width="170" sortable="custom">
            <template #default="{ row }">
              <div class="flex items-center gap-2">
                <ContainerStatusBadge
                  :status="row.state"
                  :health="healthMap[row.id]?.healthStatus ?? null"
                />
                <!-- 重启次数：健康信息自带的副产品，丢了可惜；跟着状态一起显示 -->
                <span
                  v-if="(healthMap[row.id]?.restartCount ?? 0) > 0"
                  class="text-xs text-status-warn app-tabular"
                  :title="t('container.detail.restartCount')"
                >
                  ↻ {{ healthMap[row.id]?.restartCount }}
                </span>
              </div>
              <div v-if="row.status" class="app-truncate mt-0.5 text-xs text-content-faint">
                {{ row.status }}
              </div>
            </template>
          </el-table-column>

          <el-table-column
            v-if="columns.cpu"
            :label="t('container.col.cpu')"
            width="130"
          >
            <template #default="{ row }">
              <div v-if="latestSample(row.id)" class="flex items-center gap-2">
                <span class="font-mono text-xs app-tabular">
                  {{ formatPercent(latestSample(row.id)!.cpuPercent) }}
                </span>
                <svg class="shrink-0" width="80" height="18" aria-hidden="true">
                  <polyline
                    :points="sparkline(row.id, (s) => s.cpuPercent)"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="1"
                    class="text-brand-500"
                  />
                </svg>
              </div>
              <span v-else class="text-xs text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column
            v-if="columns.memory"
            :label="t('container.col.memory')"
            width="150"
          >
            <template #default="{ row }">
              <div v-if="latestSample(row.id)" class="flex items-center gap-2">
                <span class="font-mono text-xs app-tabular">
                  {{ formatMemory(latestSample(row.id)!.memoryUsage, latestSample(row.id)!.memoryLimit) }}
                </span>
                <svg class="shrink-0" width="80" height="18" aria-hidden="true">
                  <polyline
                    :points="sparkline(row.id, (s) => (s.memoryLimit > 0 ? (s.memoryUsage / s.memoryLimit) * 100 : null))"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="1"
                    class="text-surface-accent"
                  />
                </svg>
              </div>
              <span v-else class="text-xs text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column
            v-if="columns.network"
            :label="t('container.col.network')"
            width="130"
          >
            <template #default="{ row }">
              <div v-if="latestSample(row.id)" class="flex items-center gap-2">
                <span class="font-mono text-xs app-tabular">
                  {{ formatRate(latestSample(row.id)!.networkRxBytesPerSecond) }}
                </span>
                <svg class="shrink-0" width="80" height="18" aria-hidden="true">
                  <polyline
                    :points="sparkline(row.id, (s) => s.networkRxBytesPerSecond)"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="1"
                    class="text-status-ok"
                  />
                </svg>
              </div>
              <span v-else class="text-xs text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('container.col.ports')" min-width="220">
            <template #default="{ row }">
              <div v-if="row.ports.length > 0" class="whitespace-pre-line font-mono text-xs leading-relaxed">
                {{ row.ports.map(formatPort).join('\n') }}
              </div>
              <span v-else class="text-xs text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column prop="created" :label="t('container.col.created')" width="170" sortable="custom">
            <template #default="{ row }">
              <span class="text-xs text-content-muted">{{ formatDateTime(row.created) }}</span>
            </template>
          </el-table-column>

          <!--
            文字标签收进 tooltip 后，这一列从 280px 收窄。
            `gap-0`：五个 24px 圆形按钮紧邻排列，5×24=120px 比 `gap-0.5` 的 128px 省 8px。
            ⚠️ 不用负间距（`-space-x-0.5`）压：圆形按钮相叠 24px 会让后一个完全盖住前一个的
            右半边，圆角处实际可点区更小——看着紧凑，点起来却会点不中。
            ⚠️ 也用不了 `el-button-group`：它的相邻规则是直接子选择器
            `>.el-button+.el-button`，而 IconActionButton 为 tooltip 包了一层 span，匹配不上。
            省略 `align` 即左对齐：表头「操作」必须与图标同侧，右对齐看着是错位的。
          -->
          <el-table-column :label="t('container.col.actions')" width="136">
            <template #default="{ row }">
              <div class="flex items-center">
                <IconActionButton
                  v-for="action in rowActions(row)"
                  :key="action.key"
                  :icon="action.icon"
                  :label="action.label"
                  :tone="action.tone ?? 'default'"
                  :loading="action.loading"
                  :disabled="action.disabled"
                  @click="action.run(row)"
                />
                <el-dropdown trigger="click">
                  <IconActionButton
                    icon="i-carbon-overflow-menu-horizontal"
                    :label="t('container.action.more')"
                    :disabled="busyIds.has(row.id)"
                  />
                  <template #dropdown>
                    <el-dropdown-menu>
                      <el-dropdown-item
                        v-for="action in moreActions(row)"
                        :key="action.key"
                        :disabled="action.disabled"
                        @click="action.run(row)"
                      >
                        <span :class="action.icon" aria-hidden="true" />
                        <span class="ml-2">{{ action.label }}</span>
                        <span
                          v-if="action.hint"
                          class="ml-2 text-xs text-status-warn"
                        >{{ action.hint }}</span>
                      </el-dropdown-item>
                    </el-dropdown-menu>
                  </template>
                </el-dropdown>
              </div>
            </template>
          </el-table-column>
        </el-table>

        <EmptyState
          v-if="!loading && containers.length === 0"
          :icon="includeStopped ? 'i-carbon-cube' : 'i-carbon-search'"
          :title="includeStopped ? t('container.empty.title') : t('container.empty.filtered')"
          :description="includeStopped ? t('container.empty.hint') : t('container.empty.filteredHint')"
        />

        <!-- 分页条始终显示：`el-pagination` 自带页码按钮，「现在在第几页 / 一共几页」
             是可见可点的，不再需要先猜「总数是否超过每页条数」。
             `disabled` 绑 loading：轮询与刷新期间禁掉，防止连点翻出重复页。 -->
        <ListPagination
          :total="total"
          :page-index="pageIndex"
          :page-size="pageSize"
          :disabled="loading"
          @update:page-index="goToPage"
          @update:page-size="onPageSizeChange"
        />
      </template>
    </SectionCard>

    <CreateContainerDialog
      v-model="createDialogVisible"
      :host-id="hostId"
      @created="onCreated"
    />

    <ContainerRenameDialog
      v-model="renameVisible"
      :host-id="hostId"
      :container-id="operationTarget?.id ?? null"
      :current-name="operationTarget?.name ?? ''"
      @renamed="onOperationDone"
      @update:model-value="(open) => !open && closeOperation(renameVisible)"
    />

    <ContainerUpdateDialog
      v-model="updateVisible"
      :host-id="hostId"
      :container-id="operationTarget?.id ?? null"
      @updated="onOperationDone"
      @update:model-value="(open) => !open && closeOperation(updateVisible)"
    />

    <ContainerDuplicateDialog
      v-model="duplicateVisible"
      :host-id="hostId"
      :container-id="operationTarget?.id ?? null"
      :current-name="operationTarget?.name ?? ''"
      @duplicated="onOperationDone"
      @update:model-value="(open) => !open && closeOperation(duplicateVisible)"
    />

    <ContainerAddVolumesDialog
      v-model="addVolumesVisible"
      :host-id="hostId"
      :container-id="operationTarget?.id ?? null"
      :current-name="operationTarget?.name ?? ''"
      @recreated="onOperationDone"
      @update:model-value="(open) => !open && closeOperation(addVolumesVisible)"
    />
  </div>
</template>