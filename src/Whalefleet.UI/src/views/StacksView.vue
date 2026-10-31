<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import ListPagination from '@/components/common/ListPagination.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import StatusBadge from '@/components/common/StatusBadge.vue'
import StackApplyDialog from '@/components/business/StackApplyDialog.vue'
import { useListSearch } from '@/composables/useListSearch'
import { useStackTask } from '@/composables/useStackTask'
import { deployStack, listStacks, removeStack, stopStack, updateStack } from '@/api/stack'
import { ApiRequestError } from '@/api'
import { formatDateTime } from '@/utils/datetime'
import { stackStatusKey, stackTone } from '@/utils/stack-display'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import { PAGE_SIZE_ALL, resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import type { StackListResult, StackStatus, StackSummary } from '@/types/api'

/**
 * 编排 Stack 列表页（0.10.0）。
 *
 * ## 三处与其他列表页不同
 * 1. **主体是「项目」而不是「容器」**：一行一个 Compose 项目，
 *    状态是**聚合**出来的（`running` / `partial` / `stopped` / `unknown`）。
 * 2. **有两个来源**：本平台部署的（`managed=true`）与用户自己在命令行
 *    `docker compose up` 的（`managed=false`）。后者**不能更新**——
 *    界面必须把它标出来，否则用户点了必然失败。
 * 3. **所有写操作都是异步任务**：部署要拉镜像，HTTP 请求扛不住。
 *    因此点完立刻返回任务标识，进度靠实时通道 + 轮询任务快照。
 *
 * ## 为什么「停止」与「删除」分成两个按钮
 * 停止保留容器（可随时再起），删除会清掉容器与网络（数据卷默认保留）。
 * 两者后果不同，因此分开放，且删除要二次确认。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const stacks = ref<StackSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
const pageSize = ref<number>(usePreferencesStore().pageSize)
const loading = ref(false)
const error = ref<string | null>(null)

/** 状态筛选。 */
const statusFilter = ref<StackStatus | 'all'>('all')

/** 正在执行操作的项目名：让对应按钮转圈，而不是整页转圈。 */
const busyNames = ref<Set<string>>(new Set())

/** 部署 / 更新对话框。 */
const applyVisible = ref(false)
/** 对话框模式：部署还是更新。 */
const applyMode = ref<'deploy' | 'update'>('deploy')
/** 对话框当前操作的 Stack（更新时）。 */
const applyTarget = ref<StackSummary | null>(null)

const { task, busy: taskBusy, percent, step, run } = useStackTask()

/** URL 上的 hostId。**以它为准**而不是 store.currentId：深链进入时 store 可能还没被守卫写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

const isEmpty = computed(() => !loading.value && error.value === null && stacks.value.length === 0)

/**
 * 总页数。
 *
 * 必须用 `resolvePageSize` 而不是直接 `total / pageSize`：
 * `pageSize` 可能是 `PAGE_SIZE_ALL`（0，除以它得 Infinity），也可能被上限夹紧。
 */
const pageCount = computed(() =>
  Math.max(1, Math.ceil(total.value / resolvePageSize(pageSize.value, total.value)))
)

/** 状态筛选项。 */
const statusOptions = computed(() => [
  { value: 'all', label: t('stack.filter.statusAll') },
  { value: 'running', label: t('stack.status.running') },
  { value: 'partial', label: t('stack.status.partial') },
  { value: 'stopped', label: t('stack.status.stopped') },
  { value: 'unknown', label: t('stack.status.unknown') }
])

/**
 * 列表查询参数。
 *
 * ⚠️ 「全部」必须在这里换算成真实条数再发出去：0 传出去会被后端
 * `Math.Clamp(pageSize, 1, 100)` 夹成 1，用户选了「全部」却只看到一行。
 */
function listParams() {
  return {
    // ⚠️ 搜索与筛选都在**服务端**：分页也是服务端做，前端过滤只能筛到当前页。
    search: search.value.trim(),
    status: statusFilter.value === 'all' ? undefined : statusFilter.value,
    pageIndex: pageIndex.value,
    pageSize: resolvePageSize(pageSize.value, total.value)
  }
}

/** 搜索：输入去抖 + 回车即时；筛完必须回到第 0 页。 */
const { keyword: search, schedule: scheduleSearch, searchNow } = useListSearch(async () => {
  pageIndex.value = 0
  await load()
})

async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true
  try {
    const result: StackListResult = await listStacks(hostId.value, listParams())

    // 删光当前页（如把最后一页的最后一个项目删了）时自动回退一页：
    // 否则用户会停在一片空白上，且「共 N 个」与空表同时显示，很容易被读成「全删完了」。
    if (result.items.length === 0 && result.pageIndex > 0 && result.total > 0) {
      pageIndex.value = result.pageIndex - 1
      await load()
      return
    }

    stacks.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100。
    pageIndex.value = result.pageIndex
    if (pageSize.value !== PAGE_SIZE_ALL) pageSize.value = result.pageSize

    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : t('stack.loadFailed')
  } finally {
    loading.value = false
  }
}

async function goToPage(next: number): Promise<void> {
  if (next < 0 || next >= pageCount.value) return

  pageIndex.value = next
  await load()
}

/** 换每页条数。 */
function onPageSizeChange(size: number): void {
  usePreferencesStore().setPageSize(size)
  pageSize.value = size
}

/** 换状态筛选。 */
async function onStatusFilterChange(next: StackStatus | 'all'): Promise<void> {
  statusFilter.value = next
  pageIndex.value = 0
  await load()
}

/** 打开部署对话框。 */
function openDeploy(): void {
  applyMode.value = 'deploy'
  applyTarget.value = null
  applyVisible.value = true
}

/** 打开更新对话框。 */
function openUpdate(stack: StackSummary): void {
  applyMode.value = 'update'
  applyTarget.value = stack
  applyVisible.value = true
}

/**
 * 对话框提交后的执行。
 *
 * @param payload 提交内容。
 */
async function onApply(payload: {
  name: string
  composeContent: string
  pullAlways: boolean
  pruneOrphans: boolean
}): Promise<void> {
  const isUpdate = applyMode.value === 'update'
  const target = applyTarget.value?.name ?? payload.name

  busyNames.value = new Set([...busyNames.value, target])

  const snapshot = await run(
    hostId.value,
    (taskId) =>
      isUpdate
        ? updateStack(hostId.value, {
            name: payload.name,
            composeContent: payload.composeContent,
            taskId,
            pullAlways: payload.pullAlways,
            pruneOrphans: payload.pruneOrphans
          })
        : deployStack(hostId.value, {
            name: payload.name,
            composeContent: payload.composeContent,
            taskId,
            pullAlways: payload.pullAlways,
            pruneOrphans: payload.pruneOrphans
          }),
    load
  )

  busyNames.value.delete(target)

  if (!snapshot) {
    ElMessage.error(t('stack.task.failed'))
    return
  }

  if (snapshot.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('stack.task.failed'))
    return
  }

  ElMessage.success(t('stack.task.succeeded'))
}

/** 停止编排。 */
async function stopStackByName(stack: StackSummary): Promise<void> {
  busyNames.value = new Set([...busyNames.value, stack.name])

  const snapshot = await run(
    hostId.value,
    (taskId) => stopStack(hostId.value, { name: stack.name, taskId }),
    load
  )

  busyNames.value.delete(stack.name)

  if (snapshot?.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('stack.task.failed'))
    return
  }

  if (snapshot) {
    ElMessage.success(t('stack.task.succeeded'))
  }
}

/** 删除编排。 */
async function removeStackByName(stack: StackSummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('stack.remove.confirm', { name: stack.name }),
      t('stack.remove.title'),
      {
        type: 'warning',
        confirmButtonText: t('common.confirm'),
        cancelButtonText: t('common.cancel')
      }
    )
  } catch {
    return
  }

  busyNames.value = new Set([...busyNames.value, stack.name])

  const snapshot = await run(
    hostId.value,
    (taskId) => removeStack(hostId.value, { name: stack.name, taskId, removeVolumes: false }),
    load
  )

  busyNames.value.delete(stack.name)

  if (snapshot?.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('stack.task.failed'))
    return
  }

  if (snapshot) {
    ElMessage.success(t('stack.task.succeeded'))
  }
}

/** 进入详情。 */
function openDetail(stack: StackSummary): void {
  // ⚠️ 传**原始**名字，不要自己 encodeURIComponent：vue-router 会再编码一次，
  // 详情页拿到的 id 永远对不上。
  void router.push({
    name: 'host-stack-detail',
    params: { hostId: hostId.value, name: stack.name }
  })
}

watch(
  () => route.params.hostId,
  () => {
    error.value = null
    statusFilter.value = 'all'
    pageIndex.value = 0
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-layers"
      :title="t('nav.stacks')"
      :title-tip="t('page.stacks.subtitle')"
      narrow
    >
      <template #actions>
        <el-button type="primary" @click="openDeploy">{{ t('stack.action.deploy') }}</el-button>
      </template>
    </PageHeader>

    <!--
      任务进度条：只在有任务在跑时出现。
      它挂在页面顶部而不是对话框里——用户点完「部署」后可能直接去干别的，
      进度条在这里才看得见。
    -->
    <div v-if="taskBusy" class="mb-4">
      <el-alert type="info" :closable="false" show-icon>
        <template #title>{{ t('stack.task.running') }}</template>
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

    <SectionCard density="flush">
      <template #actions>
        <ListToolbar>
          <template #filters>
            <!-- ⚠️ `!w-40` 的 `!` 不能省：Element Plus 把 `el-input` / `el-select`
                 的宽度写死成 `var(--el-input-width)` / `var(--el-select-width)`（默认 100%），
                 同特异度下 EP 赢，工具类会被吃掉。 -->
            <el-input
              v-model="search"
              class="!w-40 sm:!w-64"
              clearable
              :placeholder="t('stack.search.placeholder')"
              @input="scheduleSearch"
              @keyup.enter="searchNow"
              @clear="searchNow"
            >
              <template #prefix><span class="i-carbon-search" /></template>
            </el-input>

            <el-select
              :model-value="statusFilter"
              class="!w-32"
              @change="onStatusFilterChange"
            >
              <el-option
                v-for="option in statusOptions"
                :key="option.value"
                :label="option.label"
                :value="option.value"
              />
            </el-select>

            <span class="text-xs text-content-muted">{{ t('stack.total', { total }) }}</span>
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
          </template>
        </ListToolbar>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <template v-else>
        <EmptyState
          v-if="isEmpty"
          icon="i-carbon-layers"
          :title="t('stack.empty')"
          :description="t('stack.emptyHint')"
        />

        <el-table v-else :data="stacks" v-loading="loading" row-key="name">
          <el-table-column :label="t('stack.column.name')" min-width="180">
            <template #default="{ row }">
              <button
                type="button"
                class="text-sm font-medium text-content-strong hover:text-brand-500"
                @click="openDetail(row)"
              >
                {{ row.name }}
              </button>
              <!--
                不受管的必须显式标出来：它意味着「这个项目不是本平台部署的，
                没有 YAML，不能更新」。不标的话用户点「更新」必然失败。
              -->
              <el-tag v-if="!row.managed" size="small" type="info" class="ml-2">
                {{ t('stack.managed.external') }}
              </el-tag>
            </template>
          </el-table-column>

          <el-table-column :label="t('stack.column.status')" width="120">
            <template #default="{ row }">
              <StatusBadge :tone="stackTone(row.status)">
                {{ t(stackStatusKey(row.status)) }}
              </StatusBadge>
            </template>
          </el-table-column>

          <el-table-column :label="t('stack.column.services')" width="100">
            <template #default="{ row }">
              <span class="text-sm text-content">{{ row.serviceCount }}</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('stack.column.containers')" width="140">
            <template #default="{ row }">
              <span class="text-sm text-content">
                {{ row.runningCount }} / {{ row.containerCount }}
              </span>
            </template>
          </el-table-column>

          <el-table-column :label="t('stack.column.resources')" width="150">
            <template #default="{ row }">
              <span class="text-sm text-content-muted">
                {{
                  t('stack.resources', {
                    networks: row.networkCount,
                    volumes: row.volumeCount
                  })
                }}
              </span>
            </template>
          </el-table-column>

          <el-table-column :label="t('stack.column.updatedAt')" width="180">
            <template #default="{ row }">
              <span class="text-xs text-content-muted">
                {{ row.updatedAt ? formatDateTime(row.updatedAt) : '—' }}
              </span>
            </template>
          </el-table-column>

          <el-table-column :label="t('common.actions')" width="160" align="right">
            <template #default="{ row }">
              <div class="flex items-center justify-end gap-1">
                <IconActionButton
                  icon="i-carbon-edit"
                  :label="t('stack.action.update')"
                  placement="bottom"
                  :disabled="!row.managed || busyNames.has(row.name)"
                  :hint="row.managed ? undefined : t('stack.managed.hint')"
                  @click="openUpdate(row)"
                />
                <IconActionButton
                  icon="i-carbon-pause"
                  :label="t('stack.action.stop')"
                  placement="bottom"
                  :disabled="busyNames.has(row.name) || row.runningCount === 0"
                  @click="stopStackByName(row)"
                />
                <IconActionButton
                  icon="i-carbon-trash-can"
                  :label="t('common.delete')"
                  tone="danger"
                  placement="bottom"
                  :disabled="busyNames.has(row.name)"
                  @click="removeStackByName(row)"
                />
              </div>
            </template>
          </el-table-column>
        </el-table>
      </template>
    </SectionCard>

    <ListPagination
      v-if="!isEmpty && !error"
      :total="total"
      :page-index="pageIndex"
      :page-size="pageSize"
      :page-count="pageCount"
      @update:page-index="goToPage"
      @update:page-size="onPageSizeChange"
    />

    <StackApplyDialog
      v-model:visible="applyVisible"
      :mode="applyMode"
      :stack="applyTarget"
      @submit="onApply"
    />
  </div>
</template>
