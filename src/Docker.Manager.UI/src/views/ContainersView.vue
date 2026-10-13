<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import CreateContainerDialog from '@/components/business/CreateContainerDialog.vue'
import {
  listContainers,
  removeContainer,
  restartContainer,
  startContainer,
  stopContainer
} from '@/api/container'
import { ApiRequestError } from '@/api'
import {
  CONTAINER_TONE_CHIP,
  CONTAINER_TONE_LABEL_KEY,
  containerTone,
  formatPort
} from '@/utils/container-status'
import { formatDateTime } from '@/utils/datetime'
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
const pageSize = ref(20)
const includeStopped = ref(true)
const loading = ref(false)
/** 错误信息；非 null 时显示错误态 + 重试，而不是保留一份可能是陈旧的数据 */
const error = ref<string | null>(null)
/** 正在执行操作的容器 id 集合：让行内按钮各自转圈，而不是整页转圈 */
const busyIds = ref<Set<string>>(new Set())
const createDialogVisible = ref(false)
const lastSyncedAt = ref<number | null>(null)

let pollTimer: ReturnType<typeof setInterval> | null = null

/** URL 上的 hostId。**以它为准**而不是 store.currentId：深链进入时 store 可能还没被守卫写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const hasPrevPage = computed(() => pageIndex.value > 0)
const hasNextPage = computed(() => pageIndex.value + 1 < pageCount.value)

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
    const result = await listContainers(hostId.value, {
      all: includeStopped.value,
      pageIndex: pageIndex.value,
      pageSize: pageSize.value
    })

    containers.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100，
    // 拿请求里的值去算「还有没有下一页」会得出错误结论。
    pageIndex.value = result.pageIndex
    pageSize.value = result.pageSize
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
    const result = await listContainers(hostId.value, {
      all: includeStopped.value,
      pageIndex: pageIndex.value,
      pageSize: pageSize.value
    })

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
 * 某一行当前可用的动作（启动 / 停止二选一，由状态决定）。
 *
 * 按钮本身的 tooltip / 禁用态包裹 / 无障碍标签都在 `IconActionButton` 里，这里只负责
 * 「有哪些动作、什么图标、什么文案、点了干什么」。列成表而不是写死 5 段模板，是因为
 * 启动/停止要按状态换图标与文案，再乘以重启、删除、日志、详情，逐段展开就得在模板里
 * 散落着判断；列成表后一眼能看全，以后加动作（复制 ID / 交互式终端）只加一行。
 *
 * ⚠️ 新加图标必须同步写进 `uno.config.ts` 的 safelist（那里会校验图标在 Carbon 中真实存在）。
 */
function rowActions(container: ContainerSummary): RowAction[] {
  const busy = busyIds.value.has(container.id)
  const active = isActive(container)

  return [
    {
      key: 'power',
      icon: active ? 'i-carbon-stop-filled' : 'i-carbon-play-filled',
      label: t(active ? 'container.action.stop' : 'container.action.start'),
      loading: busy,
      run: active ? onStop : onStart
    },
    {
      key: 'restart',
      icon: 'i-carbon-renew',
      label: t('container.action.restart'),
      disabled: busy,
      run: onRestart
    },
    {
      key: 'remove',
      icon: 'i-carbon-trash-can',
      label: t('container.action.remove'),
      tone: 'danger',
      disabled: busy,
      run: onRemove
    },
    {
      key: 'logs',
      icon: 'i-carbon-terminal',
      label: t('container.action.logs'),
      run: openLogs
    },
    {
      key: 'detail',
      icon: 'i-carbon-document-view',
      label: t('container.action.detail'),
      run: openInspect
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
        <el-table
          v-loading="loading"
          :data="containers"
          :row-key="(row: ContainerSummary) => row.id"
          empty-text=""
        >
          <el-table-column :label="t('container.col.name')" min-width="200">
            <template #default="{ row }">
              <div class="min-w-0">
                <div class="app-truncate font-medium text-content-strong">{{ row.name }}</div>
                <div class="app-truncate font-mono text-xs text-content-faint">{{ row.id.slice(0, 12) }}</div>
              </div>
            </template>
          </el-table-column>

          <el-table-column :label="t('container.col.image')" min-width="180">
            <template #default="{ row }">
              <span class="app-truncate text-sm">{{ row.image }}</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('container.col.state')" width="160">
            <template #default="{ row }">
              <span
                class="inline-flex items-center rounded-full px-2 py-0.5 text-xs ring-1 ring-inset"
                :class="CONTAINER_TONE_CHIP[containerTone(row.state)]"
              >
                {{ t(CONTAINER_TONE_LABEL_KEY[containerTone(row.state)]) }}
              </span>
              <div v-if="row.status" class="app-truncate mt-0.5 text-xs text-content-faint">
                {{ row.status }}
              </div>
            </template>
          </el-table-column>

          <el-table-column :label="t('container.col.ports')" min-width="200">
            <template #default="{ row }">
              <div v-if="row.ports.length > 0" class="whitespace-pre-line font-mono text-xs leading-relaxed">
                {{ row.ports.map(formatPort).join('\n') }}
              </div>
              <span v-else class="text-xs text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('container.col.created')" width="170">
            <template #default="{ row }">
              <span class="text-xs text-content-muted">{{ formatDateTime(row.created) }}</span>
            </template>
          </el-table-column>

          <!-- 文字标签收进 tooltip 后，这一列从 280px 收窄到 180px -->
          <el-table-column :label="t('container.col.actions')" width="180" align="right">
            <template #default="{ row }">
              <div class="flex items-center justify-end gap-0.5">
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

        <!-- 分页条只在超过一页时出现：5 个容器时放一条分页器只会让人去找下一页在哪。 -->
        <div
          v-if="total > pageSize"
          class="flex items-center justify-between gap-3 border-t border-line-subtle px-4 py-3 sm:px-5"
        >
          <span class="text-xs text-content-muted">{{ t('container.total', { total }) }}</span>
          <div class="flex items-center gap-2">
            <el-button size="small" :disabled="!hasPrevPage" @click="goToPage(pageIndex - 1)">
              {{ t('common.prev') }}
            </el-button>
            <span class="text-xs text-content-muted">{{ pageIndex + 1 }} / {{ pageCount }}</span>
            <el-button size="small" :disabled="!hasNextPage" @click="goToPage(pageIndex + 1)">
              {{ t('common.next') }}
            </el-button>
          </div>
        </div>
      </template>
    </SectionCard>

    <CreateContainerDialog
      v-model="createDialogVisible"
      :host-id="hostId"
      @created="onCreated"
    />
  </div>
</template>