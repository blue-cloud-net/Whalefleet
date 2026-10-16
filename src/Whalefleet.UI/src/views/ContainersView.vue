<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch } from 'vue'
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
import ContainerStatusBadge from '@/components/business/ContainerStatusBadge.vue'
import { onActionRequest } from '@/composables/useActionBus'
import {
  batchRemoveContainers,
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
import { PAGE_SIZE_ALL, resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import type { ContainerSummary } from '@/types/api'

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
const includeStopped = ref(true)
const loading = ref(false)
/** 错误信息；非 null 时显示错误态 + 重试，而不是保留一份可能是陈旧的数据 */
const error = ref<string | null>(null)
/** 正在执行操作的容器 id 集合：让行内按钮各自转圈，而不是整页转圈 */
const busyIds = ref<Set<string>>(new Set())
const createDialogVisible = ref(false)
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

watch(
  hostId,
  (id) => {
    if (!id) return

    stopPolling()
    pageIndex.value = 0
    error.value = null
    // 换主机等于换了一批完全不同的行：不清选择的话，批量删除会作用到上一台主机的容器上
    selected.value = []
    void load().then(startPolling)
  },
  { immediate: true }
)

onMounted(() => {
  // 从后台切回来时立刻补一轮：否则用户会盯着暂停前那一份数据，而空白可能长达几分钟。
  const onVisible = () => {
    if (!document.hidden) void poll()
  }
  document.addEventListener('visibilitychange', onVisible)
  onBeforeUnmount(() => document.removeEventListener('visibilitychange', onVisible))
})

onBeforeUnmount(stopPolling)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-container-software"
      :title="t('nav.containers')"
      :subtitle="t('page.containers.subtitle')"
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
                -->
                <button
                  type="button"
                  class="app-truncate block w-fit max-w-full text-left font-medium text-content-strong hover:text-brand-500 hover:underline"
                  @click="openInspect(row)"
                >
                  {{ row.name }}
                </button>
                <div class="app-truncate font-mono text-xs text-content-faint">{{ row.id.slice(0, 12) }}</div>
              </div>
            </template>
          </el-table-column>

          <el-table-column prop="image" :label="t('container.col.image')" min-width="200" sortable="custom">
            <template #default="{ row }">
              <span class="app-truncate text-sm">{{ row.image }}</span>
            </template>
          </el-table-column>

          <el-table-column prop="state" :label="t('container.col.state')" width="160" sortable="custom">
            <template #default="{ row }">
              <ContainerStatusBadge :status="row.state" />
              <div v-if="row.status" class="app-truncate mt-0.5 text-xs text-content-faint">
                {{ row.status }}
              </div>
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
              </div>
            </template>
          </el-table-column>
        </el-table>

        <EmptyState
          v-if="!loading && containers.length === 0"
          :icon="includeStopped ? 'i-carbon-container-software' : 'i-carbon-search'"
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
  </div>
</template>