<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import ListPagination from '@/components/common/ListPagination.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import CreateContainerDialog from '@/components/business/CreateContainerDialog.vue'
import ContainerBatchActions from '@/components/business/ContainerBatchActions.vue'
import ContainerListTable from '@/components/business/ContainerListTable.vue'
import ContainerOperationDialogs from '@/components/business/ContainerOperationDialogs.vue'
import TransferExportDialog from '@/components/business/TransferExportDialog.vue'
import { onActionRequest } from '@/composables/useActionBus'
import { useListSearch } from '@/composables/useListSearch'
import {
  getContainerHealth,
  killContainer,
  listContainers,
  pauseContainer,
  removeContainer,
  restartContainer,
  resumeContainer,
  startContainer,
  stopContainer,
  type ContainerSortField,
  type ContainerStateFilter
} from '@/api/container'
import { ApiRequestError } from '@/api'
import { canPerform, isActive, type ContainerAction } from '@/utils/container-actions'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import { useContainerStatsBoard } from '@/composables/useContainerStatsBoard'
import { subscribeContainerDied } from '@/realtime/notification'
import {
  CONTAINER_BASIC_COLUMNS,
  CONTAINER_METRIC_COLUMNS,
  PAGE_SIZE_ALL,
  usePreferencesStore,
  type ContainerColumn
} from '@/stores/preferences'
import type {
  ContainerHealthSummary,
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

/** 偏好 store（实时更新开关、列开关、状态筛选）。 */
const prefs = usePreferencesStore()

/**
 * 状态筛选。
 *
 * ⚠️ **初值取自用户偏好**（持久化）：状态筛选是「我这次要看哪一类容器」的延续，
 * 不是一次性开关——用户切到「仅未运行」排查完问题后，下次进页面往往还是想看那类。
 */
const stateFilter = ref<ContainerStateFilter>(prefs.containerStateFilter)

const loading = ref(false)
/** 错误信息；非 null 时显示错误态 + 重试，而不是保留一份可能是陈旧的数据 */
const error = ref<string | null>(null)
/** 正在执行操作的容器 id 集合：让行内按钮各自转圈，而不是整页转圈 */
const busyIds = ref<Set<string>>(new Set())
const createDialogVisible = ref(false)

/**
 * 0.3.0 的四个对话框与 0.7.0 新增的三个共用一组「当前操作对象」状态。
 *
 * ⚠️ 不用八个独立的 visible + id，而是共用一个 `target`：
 * 八个对话框分属八组状态时，会出现「A 对话框开着，B 的 id 却指到了另一个容器」的组合——
 * 而那种状态下用户点确认，操作会作用在**错的容器**上。
 * 一个 `target` 从构造上排除了这种组合。
 */
const operationTarget = ref<ContainerSummary | null>(null)

/** 对话框宿主（七个操作对话框的唯一挂载点）。 */
const operationDialogs = ref<InstanceType<typeof ContainerOperationDialogs> | null>(null)

/** 当前排序列；`null` = 不排序，后端保持守护进程返回顺序（创建时间降序） */
const sortField = ref<ContainerSortField | null>(null)
const sortOrder = ref<'asc' | 'desc'>('desc')

/** 批量操作选中的容器；`el-table` 的 selection 是受控的，得自己留一份才能算数量 */
const selected = ref<ContainerSummary[]>([])

/**
 * 选中容器的状态列表（供动作可用性判定）。
 *
 * ⚠️ **不要在这里过滤**：判定逻辑自己知道哪些状态满足前置条件
 * （批量时「一个都不满足」才禁用），而过滤过的列表会让它误以为全部满足。
 */
const selectedStates = computed(() => selected.value.map((container) => container.state))
const batchBusy = ref(false)

/**
 * 表格组件实例：只有 `clearSelection()` 用得到。
 *
 * ⚠️ 它是**组件实例**而不是 `el-table` 实例——表格本体在
 * `ContainerListTable` 内部，因此清空选择要通过组件暴露的方法转发。
 */
const tableRef = ref<InstanceType<typeof ContainerListTable> | null>(null)

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
 * 「全部」固定只有一页。
 */
const pageCount = computed(() =>
  pageSize.value === PAGE_SIZE_ALL ? 1 : Math.max(1, Math.ceil(total.value / pageSize.value))
)

/**
 * 列表查询参数（`load` 与 `poll` 共用）。
 *
 * 抽出来是因为这两处一旦各写一遍，排序参数就会只在其中一处漏掉——
 * 而症状是「刚点完排序是对的，5 秒后自己跳回原顺序」，极难自查。
 */
function listParams() {
  return {
    // ⚠️ **搜索与筛选都在服务端**：分页也在服务端内存里做，
    // 前端过滤只能筛到当前页——页脚说 100 个、翻过去只剩 3 个。
    search: search.value.trim(),
    state: stateFilter.value,
    pageIndex: pageIndex.value,
    ...(pageSize.value === PAGE_SIZE_ALL ? { unpaged: true } : { pageSize: pageSize.value }),
    ...(sortField.value ? { sortBy: sortField.value, sortDir: sortOrder.value } : {})
  }
}

/**
 * 搜索 / 筛选变了之后的重新加载。
 *
 * ⚠️ **必须回到第 0 页**：筛完还停在第 3 页，结果一定是空的——
 * 而用户看到的是一个空列表，会以为「这台机器上没有容器了」。
 *
 * ⚠️ **必须清空选择**：选中的行可能已经被筛掉了，
 * 留着它们会让「批量删除」作用在**看不见的容器**上。
 */
async function reloadAfterFilterChange(): Promise<void> {
  pageIndex.value = 0
  clearSelection(tableRef.value)
  await load()
}

/**
 * 关键字搜索（输入去抖 + 回车即时）。
 *
 * ⚠️ **必须解构成顶层绑定**：`<script setup>` 只对**顶层**的 ref 做模板解包。
 * 写成 `const search = useListSearch(...)` 时，模板里的 `search.keyword` 拿到的是
 * **Ref 对象本身**——`v-model` 会把输入框的值写成 "[object Object]"，
 * 而脚本里的 `search.keyword.trim()` 直接抛 `undefined (reading 'trim')`。
 */
const { keyword: search, schedule: scheduleSearch, searchNow } = useListSearch(
  reloadAfterFilterChange
)

/**
 * 切换状态筛选。
 *
 * 筛选值**记住**（写进用户偏好）：见 `stateFilter` 的说明。
 */
async function onStateFilterChange(next: ContainerStateFilter): Promise<void> {
  stateFilter.value = next
  prefs.setContainerStateFilter(next)
  await reloadAfterFilterChange()
}

/**
 * 容器是否处于「活动」一侧（可被停止 / 重启）。
 *
 * ⚠️ 判据不能只是 `state === 'running'`：**已暂停的容器在 Docker 语义里仍是运行中**，
 * 对它显示「启动」既不符合事实（它没停），点下去也只会得到一次「目标状态已达成」的
 * 空操作——用户会以为按钮失灵。过渡态（paused / restarting / removing）全部归到「活动」一侧，
 * 因此这里复用色调映射，而不是再列一次状态名（列两次早晚会漏一个）。
 *
 * ✅ 判定本身放在 `utils/container-actions.ts`（与「哪些动作可做」共用一份），
 * 列表页 / 详情页 / 批量条三处都必须给出**同一个答案**。
 */
function isActiveContainer(container: ContainerSummary): boolean {
  return isActive(container.state)
}

/**
 * 容器是否处于「已暂停」状态。
 *
 * ⚠️ 单独一个判断而不是用色调：色调把 paused 与 restarting 归为同一类「过渡态」，
 * 而这两个状态要的动作完全不同（暂停的能「继续」，重启中的不能）。
 */
function isPaused(container: ContainerSummary): boolean {
  return container.state === 'paused'
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

/**
 * 列键 → 词条键。
 *
 * ⚠️ **穷举式 `Record`，不在模板里拼 `t(`container.col.${col}`)`**（见 AGENTS.md）：
 * 拼字符串时少一条词条也不会报错，界面直接显示 `container.col.created` 这种原始路径。
 * 写成 `Record<ContainerColumn, string>` 后，新增列时 TypeScript 会逼着补齐这一行。
 */
const COLUMN_LABELS: Record<ContainerColumn, string> = {
  image: 'container.col.image',
  state: 'container.col.state',
  ports: 'container.col.ports',
  created: 'container.col.created',
  cpu: 'container.col.cpu',
  memory: 'container.col.memory',
  network: 'container.col.network'
}

/** 列选择面板的两个分组（顺序即渲染顺序）。 */
const columnGroups = [
  { key: 'basic', label: 'container.columns.basic', columns: CONTAINER_BASIC_COLUMNS },
  { key: 'metrics', label: 'container.columns.metrics', columns: CONTAINER_METRIC_COLUMNS }
] as const

/** 分批补充当前结果的健康信息，每批遵守端点上限。 */
async function refreshHealth(): Promise<void> {
  if (!hostId.value) return

  const host = hostId.value
  const rows = containers.value
  const ids = rows.map((container) => container.id)
  if (ids.length === 0) return

  try {
    const next: Record<string, ContainerHealthSummary> = {}
    for (let offset = 0; offset < ids.length; offset += 100) {
      const result = await getContainerHealth(host, ids.slice(offset, offset + 100))
      if (hostId.value !== host || containers.value !== rows) return
      for (const item of result.items) next[item.containerId] = item
    }

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

/**
 * 空态的图标 / 标题 / 描述。
 *
 * ⚠️ **分三种情况**：「没搜过且没筛选」= 本来就没有；
 * 「搜了或筛了」= 条件太窄。用户搜了半天看到「该主机上还没有任何容器」，
 * 会以为机器上真的没有容器——而实际只是关键字没对上。
 */
const emptyIcon = computed(() =>
  hasFilter.value ? 'i-carbon-search' : 'i-carbon-cube'
)

const emptyTitle = computed(() =>
  hasFilter.value ? t('container.empty.filtered') : t('container.empty.title')
)

const emptyDescription = computed(() =>
  hasFilter.value ? t('container.empty.filteredHint') : t('container.empty.hint')
)

/** 当前是否带着「搜索关键字」或「状态筛选」。 */
const hasFilter = computed(
  () => search.value.trim().length > 0 || stateFilter.value !== 'all'
)

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

/** 暂停容器（冻结进程，不释放 CPU 以外的资源）。 */
async function onPause(container: ContainerSummary): Promise<void> {
  await runAction(container, () => pauseContainer(hostId.value, container.id))
}

/** 恢复容器（解除暂停，容器随即继续跑）。 */
async function onResume(container: ContainerSummary): Promise<void> {
  await runAction(container, () => resumeContainer(hostId.value, container.id))
}

/**
 * 终止容器（等价于 `docker kill`：不等待优雅退出）。
 *
 * ⚠️ **单条走信号对话框**：默认 SIGKILL，但也允许指定别的信号，
 * 而非终止信号只是「发个信号」、容器可能继续跑——那句话必须让用户先看到。
 */
function onKill(container: ContainerSummary): void {
  operationTarget.value = container
  operationDialogs.value?.openKill([{ id: container.id, name: container.name }])
}

/**
 * 删除容器。
 *
 * 运行中的容器需要 `force`，而 force 会跳过优雅停止——容器内未落盘的数据会丢，
 * 这是不可逆的。因此默认**不**勾选强制，提示必须出现在确认框里而不是藏进按钮 tooltip。
 */
async function onRemove(container: ContainerSummary): Promise<void> {
  const running = isActiveContainer(container)

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
 * ⚠️ **全部动作都逐条调用**（0.7.0）：服务端不再有容器批量端点。
 * 逐条调用的代价只是几个来回，而它换来了「哪一条失败、为什么」——
 * 那是批量操作里唯一真正重要的信息。
 *
 * ⚠️ **失败项必须带上原因**（0.7.0）：只列名字的话，用户看到
 * 「web-api 失败」却不知道是「容器不在了」还是「端口被占」——
 * 两条原因对应的下一步完全不同，而界面上无法区分。
 */
async function batchRun(
  label: string,
  action: (container: ContainerSummary) => Promise<void>
): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  batchBusy.value = true
  const failed: { name: string; reason: string }[] = []

  try {
    // 并发执行：容器数受页大小限制（≤100），且这些操作互不依赖，
    // 串行会让"删 100 个"变成 100 次往返。Promise.allSettled 而不是 all——
    // 一个 reject 会让整个 all 提前抛，剩下的请求还在飞，结果就说不清了。
    await Promise.allSettled(
      targets.map(async (container) => {
        try {
          await action(container)
        } catch (e) {
          failed.push({
            name: container.name,
            reason: e instanceof Error ? e.message : String(e)
          })
        }
      })
    )

    const done = targets.length - failed.length
    if (failed.length === 0) {
      ElMessage({ type: 'success', message: t('container.batch.done', { action: label, count: done }) })
    } else {
      // 名字 + 原因一起报出来：原因才是用户下一步的依据
      const details = failed.map((item) => `${item.name}（${item.reason}）`).join('、')

      ElMessage({
        type: 'warning',
        message: t('container.batch.partial', {
          action: label,
          done,
          failed: failed.length,
          names: details
        }),
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

async function batchPause(): Promise<void> {
  await batchRun(t('container.action.pause'), (c) => pauseContainer(hostId.value, c.id))
}

async function batchResume(): Promise<void> {
  await batchRun(t('container.action.resume'), (c) => resumeContainer(hostId.value, c.id))
}

/**
 * 批量删除（逐条调 `remove`）。
 *
 * ⚠️ **不走服务端批量端点**：容器侧已不再提供 `batch-remove`，
 * 逐条调用才能给出「哪一条失败、为什么」。
 *
 * 与单个删除同一条原则——不可逆，必须确认；但确认框里要说明两件单条版没说的事：
 *  1. **有多少个正在运行**（force 会跳过优雅停止，数据会丢）
 *  2. **一次性删 N 个**，让人有机会发现勾选范围不对（批量操作最常见的事故）
 * 不给「取消勾选强制」——N 个容器逐个确认强删，交互成本高到会让人闭眼点确认。
 *
 * 失败是**逐条汇报**的：`batchRun` 会把每一条失败连原因一起列出来。
 */
async function batchRemove(): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  const running = targets.filter((c) => isActive(c.state))

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

  await batchRun(t('container.action.remove'), (c) =>
    removeContainer(hostId.value, c.id, isActive(c.state))
  )
}

/**
 * 批量动作的统一入口（工具条按动作类型分发）。
 *
 * ⚠️ **一处分发而不是每个按钮各绑各的**：动作表（`ContainerBatchActions`）与执行逻辑
 * 分处两个文件，靠一张 `ContainerAction` 联合类型对齐；
 * 若各按钮各绑各的，加一个动作要改两处，而漏改的那处症状是
 * 「按钮出现了但点了没反应」。
 */
async function onBatchAction(action: ContainerAction): Promise<void> {
  switch (action) {
    case 'start':
      await batchStart()
      break
    case 'stop':
      await batchStop()
      break
    case 'restart':
      await batchRestart()
      break
    case 'pause':
      await batchPause()
      break
    case 'resume':
      await batchResume()
      break
    case 'kill':
      await batchKill()
      break
    case 'remove':
      await batchRemove()
      break
  }
}

/**
 * 批量终止（SIGKILL）。
 *
 * ⚠️ 走的是**逐条**删除接口而不是批量端点——服务端已不再提供容器批量端点，
 * 而逐条调用能给出「哪一条失败、为什么」。
 */
async function batchKill(): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('container.batch.killConfirm.message', { count: targets.length }))
      ]),
      t('container.batch.killConfirm.title'),
      {
        confirmButtonText: t('container.batch.killConfirm.submit'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  await batchRun(t('container.action.kill'), (c) => killContainer(hostId.value, c.id, 'SIGKILL'))
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
  /** 禁用原因（tooltip）；不写则用 `label` */
  hint?: string
  run: (container: ContainerSummary) => void
}

/**
 * 导出（0.14.0）。
 *
 * 容器导出（`docker export`）导出的是**文件系统快照**，不含镜像历史、不含卷内容，
 * 也不含容器的运行配置——要「完整复制一个容器」请走「生成 docker run 命令」。
 * 界面必须在对话框里说清这一点，否则用户会拿它当备份。
 */
function openExport(container: ContainerSummary): void {
  exportTarget.value = container
  exportDialogVisible.value = true
}

/** 导出对话框状态。 */
const exportDialogVisible = ref(false)
const exportTarget = ref<ContainerSummary | null>(null)

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
      label: t(paused ? 'container.action.resume' : active ? 'container.action.stop' : 'container.action.start'),
      loading: busy,
      run: paused ? onResume : active ? onStop : onStart
    },
    {
      // 用 restart 而非 renew：renew 是双箭头**对向**环绕，是「刷新 / 同步」的标准符号，
      // 与页面右上角的刷新按钮（也是 renew）撞成同一个形状；restart 的两枚箭头同向，
      // 才读得出「从头再跑一遍」。
      key: 'restart',
      icon: 'i-carbon-restart',
      label: t('container.action.restart'),
      disabled: busy || paused,
      hint: paused ? t('container.action.disabled.restart') : undefined,
      run: onRestart
    },
    {
      key: 'logs',
      icon: 'i-carbon-terminal',
      label: t('container.action.logs'),
      run: openLogs
    },
    {
      // 导出（0.14.0）：只读动作，因此不吃 `busy`（写权限）——
      // 只读用户同样该能把自己的容器带走。
      key: 'export',
      icon: 'i-carbon-download',
      label: t('container.action.export'),
      run: openExport
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
  const busy = busyIds.value.has(container.id) || !container.effectivePermissions.includes('container.write')

  return [
    {
      key: 'pause',
      icon: 'i-carbon-pause',
      label: t('container.action.pause'),
      disabled: busy || !canPerform('pause', container.state),
      run: onPause
    },
    {
      key: 'resume',
      icon: 'i-carbon-play',
      label: t('container.action.resume'),
      disabled: busy || !canPerform('resume', container.state),
      run: onResume
    },
    {
      key: 'kill',
      icon: 'i-carbon-warning',
      label: t('container.action.kill'),
      disabled: busy || !canPerform('kill', container.state),
      run: onKill
    },
    {
      key: 'rename',
      icon: 'i-carbon-text-align-left',
      label: t('container.action.rename'),
      disabled: busy,
      run: (c) => openOperation(c, () => operationDialogs.value?.openRename())
    },
    {
      key: 'update',
      icon: 'i-carbon-settings-adjust',
      label: t('container.action.update'),
      disabled: busy,
      run: (c) => openOperation(c, () => operationDialogs.value?.openUpdate())
    },
    {
      key: 'duplicate',
      icon: 'i-carbon-copy',
      label: t('container.action.duplicate'),
      disabled: busy,
      run: (c) => openOperation(c, () => operationDialogs.value?.openDuplicate())
    },
    {
      key: 'recreate',
      icon: 'i-carbon-renew',
      label: t('container.action.recreate'),
      // ⚠️ 危险色 + 副标题：它与「追加挂载」一样会**替换**容器，
      // 而菜单里没有二次确认对话框的停留时间，必须一眼看出它不一样。
      hint: t('container.recreate.warning'),
      tone: 'warning',
      disabled: busy,
      run: (c) => openOperation(c, () => operationDialogs.value?.openRecreate())
    },
    {
      key: 'edit',
      icon: 'i-carbon-edit',
      label: t('container.action.edit'),
      hint: t('container.recreate.warning'),
      tone: 'warning',
      disabled: busy,
      run: (c) => openOperation(c, () => operationDialogs.value?.openEdit())
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
      run: (c) => openOperation(c, () => operationDialogs.value?.openAddVolumes())
    }
  ]
}

/**
 * 打开某个操作对话框，并把它绑定到 `container`。
 *
 * ⚠️ **先设 `target` 再开对话框**：对话框读的是 `target`，
 * 顺序反了就会拿到上一个容器——那是最难被发现的一类 bug。
 */
function openOperation(container: ContainerSummary, open: () => void): void {
  operationTarget.value = container
  open()
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
        <!--
          页头只留「新建容器」：
          刷新、搜索、筛选、批量动作都收进下方卡片头部的工具条——
          它们都是**列表的**控件，与表格挨在一起才用得顺手；
          放在页头会让它们离表格太远（中间隔着一整块卡片标题区）。
        -->
        <el-button type="primary" @click="createDialogVisible = true">
          {{ t('container.action.create') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <!--
          列表工具条（搜索 + 状态筛选 + 批量动作 + 刷新 + 显示列）。

          ⚠️ 批量按钮**常驻**：早先是「选中才出现」，
          那种模型的入口不可发现——页面上没有任何东西提示「勾选行之后能批量操作」。
          现在按钮一直在那儿，没勾选时禁用（禁用原因写在 tooltip 里）。
        -->
        <ListToolbar :selected-count="selected.length" :busy="batchBusy">
          <template #filters>
            <!--
              搜索框：输入去抖（300ms）+ 回车即时 + 可清空。
              ⚠️ `!w-40` 的 `!` 不能省：Element Plus 把 `el-input` 的宽度写死成
              `var(--el-input-width)`（默认 100%），同特异度下 EP 赢，工具类会被吃掉。
            -->
            <el-input
              v-model="search"
              class="!w-40 sm:!w-56"
              clearable
              :placeholder="t('container.search.placeholder')"
              @input="scheduleSearch"
              @keyup.enter="searchNow"
              @clear="searchNow"
            >
              <template #prefix><span class="i-carbon-search" /></template>
            </el-input>

            <!-- 状态筛选：三档（全部 / 仅运行中 / 仅未运行）。 -->
            <el-select
              :model-value="stateFilter"
              class="!w-32"
              :placeholder="t('container.filter.placeholder')"
              @change="onStateFilterChange"
            >
              <el-option value="all" :label="t('container.filter.all')" />
              <el-option value="running" :label="t('container.filter.running')" />
              <el-option value="stopped" :label="t('container.filter.stopped')" />
            </el-select>
          </template>

          <template #batch="{ count, busy }">
            <!--
              七个批量动作（启动 / 停止 / 重启 / 删除 / 终止 / 暂停 / 继续）。

              ⚠️ **常驻**：早先是「选中才出现」，
              那种模型的入口不可发现——页面上没有任何东西提示「勾选行之后能批量操作」。
              现在按钮一直在那儿，没勾选时禁用（禁用原因写在 tooltip 里）。

              ⚠️ **自适应**：宽度不够时低频的三个（终止 / 暂停 / 继续）收进「更多」下拉，
              避免工具条折行后被读成「筛选栏的第二行」。详见 `ContainerBatchActions`。
            -->
            <ContainerBatchActions
              :states="selectedStates"
              :busy="busy || count === 0"
              @run="onBatchAction"
            />
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
                <el-button
                  text
                  circle
                  :loading="loading"
                  :aria-label="t('common.refresh')"
                  @click="load"
                >
                  <template #icon><span class="i-carbon-renew" /></template>
                </el-button>
              </span>
            </el-tooltip>

            <!--
              显示列（0.6.0 起；0.12.0 扩到基础信息列）。
              ⚠️ 指标列默认全关：每开一列，每个可见容器就多占一条到守护进程的流。
              ⚠️ 健康**不在这里**：它已并入「状态」列（见 ContainerStatusBadge），
              再给一个独立开关等于让人以为「关掉就不看健康了」——实际上关不掉。
              ⚠️ 名称列**不在开关里**：它可是行身份 + 进详情的唯一入口。

              压成图标放在工具条最右：它是个「偶尔用一次」的低频开关，
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
            <el-popover placement="bottom-end" :width="240" trigger="click">
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

              <!--
                两组分开：基础信息列**只影响渲染**（关掉不省任何请求），
                而指标列每开一个就多一条到守护进程的流。
                成本模型不同的东西混成一列复选框，用户会以为它们在等价地「多占一点资源」。

                `gap-3` 放在**外层**而不是每个分组自己身上：分组之间的间隔要比
                组标题与它自己的复选框之间更大，否则第二个组标题看起来像是第一个组的最后一项。
              -->
              <div class="flex flex-col gap-3">
                <div v-for="group in columnGroups" :key="group.key" class="flex flex-col gap-1.5">
                  <p class="text-xs font-medium text-content-muted">
                    {{ t(group.label) }}
                  </p>
                  <el-checkbox
                    v-for="column in group.columns"
                    :key="column"
                    :model-value="columns[column]"
                    @change="prefs.setContainerColumn(column, $event as boolean)"
                  >
                    {{ t(COLUMN_LABELS[column]) }}
                  </el-checkbox>
                </div>
              </div>

              <!--
                名称列不可关：它是行身份 + 进详情的唯一入口。
                不说清楚的话，用户在面板里找不到「名称」会以为漏了一项。
              -->
              <p class="mt-2 text-xs text-content-faint">
                {{ t('container.columns.nameFixed') }}
              </p>

              <!-- 代价说明放在面板里：图标按钮的 tooltip 只有一行，说不下「为什么默认关闭」 -->
              <p class="mt-2 border-t border-line-subtle pt-2 text-xs text-content-faint">
                {{ t('container.columns.hint') }}
              </p>
            </el-popover>
          </template>
        </ListToolbar>
      </template>

      <!-- 错误态给整块内容位，而不是只弹一个 toast：
           容器页是「要看清单」的场景，弹完就没了的提示等于没说。 -->
      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <template v-else>
        <!--
          表格本体抽到了 `ContainerListTable`（0.10.0）：
          编排详情页要展示「按 compose 项目过滤出来的容器」，
          两处必须**渲染得一模一样**——抄第二遍的话，
          容器列表页改了端口展示规则，编排详情页不会跟着改，
          用户会在两个页面看到同一批容器的不同信息量。

          行动作列由本页提供（插槽）：两个页面的动作不一样
          （本页有「删除」与「更多」下拉，编排页给的是「终端」）。
        -->
        <ContainerListTable
          ref="tableRef"
          :containers="containers"
          :health-map="healthMap"
          :series="series"
          :columns="columns"
          :loading="loading"
          @open-detail="openInspect"
          @selection-change="onSelectionChange"
          @sort-change="onSortChange"
        >
          <template #row-actions="{ row }">
            <IconActionButton
              v-for="action in rowActions(row)"
              :key="action.key"
              :icon="action.icon"
              :label="action.label"
              :tone="action.tone ?? 'default'"
              :loading="action.loading"
              :disabled="action.disabled"
              size="medium"
              @click="action.run(row)"
            />
            <!--
              ⚠️ **下拉的触发器必须是根节点为单一元素的组件**（这里用 `el-button`），
              **不能直接放 `IconActionButton`**。

              原因：EP 的 `el-dropdown` 把默认插槽交给 `ElOnlyChild`，
              它对插槽里的第一个节点 `cloneVNode` 并挂上 **forwardRef 运行时指令**，
              靠指令的 `mounted` 钩子拿到真正的 DOM 元素。
              而 `IconActionButton` 的根是 `el-tooltip`（→ `ElPopper`）——
              **多根的 fragment**。指令挂到组件上会告警
              「Runtime directive used on component with non-element root node」，
              并且**指令不生效** → 下拉拿不到触发元素 → **菜单永远点不开**。
              （与 `el-popover` 的 reference 插槽是同一个坑，见 AGENTS.md 陷阱 49。）

              `el-button` 的根是 `<button>`（单一元素），指令能正常落到 DOM 上。
              tooltip 挂**按钮内部的图标**上，与本文件「显示列」按钮的写法一致。
            -->
            <el-dropdown trigger="click">
              <el-button
                text
                circle
                size="small"
                class="!w-7 !h-7 !p-0 !text-base"
                :aria-label="t('container.action.more')"
                :disabled="busyIds.has(row.id)"
              >
                <el-tooltip
                  :content="t('container.action.more')"
                  placement="top"
                  :show-after="TIP_DELAY_MS"
                >
                  <span class="i-carbon-overflow-menu-horizontal" />
                </el-tooltip>
              </el-button>
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
          </template>
        </ContainerListTable>

        <!--
          空态分三种：**没搜 / 搜不到 / 本来就没有**。
          混成一个「没有容器」的话，用户搜了半天看到这句话会以为机器上真没有容器——
          而实际只是关键字没对上。
        -->
        <EmptyState
          v-if="!loading && containers.length === 0"
          :icon="emptyIcon"
          :title="emptyTitle"
          :description="emptyDescription"
        />

        <!-- 分页条始终显示：`el-pagination` 自带页码按钮，「现在在第几页 / 一共几页」
             是可见可点的，不再需要先猜「总数是否超过每页条数」。
             `disabled` 绑 loading：轮询与刷新期间禁掉，防止连点翻出重复页。 -->
        <ListPagination
          supports-unpaged
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

    <!--
      七个操作对话框的**统一宿主**（0.7.0）。

      ⚠️ 详情页也挂同一个组件——两处各挂一次七个对话框会让「加一个操作」变成
      「改两个页面」，而漏改的那个症状是「列表页有、详情页没有」。
    -->
    <ContainerOperationDialogs
      :host-id="hostId"
      :target="operationTarget"
      @done="onOperationDone"
      @recreated="onOperationDone"
    />

    <TransferExportDialog
      v-model="exportDialogVisible"
      :host-id="hostId"
      kind="container"
      :resource-id="exportTarget?.id ?? ''"
      :resource-name="exportTarget?.name"
    />
  </div>
</template>