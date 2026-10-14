<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
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
import PullImageDialog from '@/components/business/PullImageDialog.vue'
import ImageSize from '@/components/business/ImageSize.vue'
import { onActionRequest } from '@/composables/useActionBus'
import { listImages, removeImage, type ImageSortField } from '@/api/image'
import { ApiRequestError } from '@/api'
import {
  DANGLING_IMAGE_LABEL,
  isDangling,
  primaryImageName,
  shortImageId,
  stackedTagCount
} from '@/utils/image-display'
import { formatDateTime } from '@/utils/datetime'
import { PAGE_SIZE_ALL, resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import type { ImageSummary } from '@/types/api'

/**
 * 镜像列表页（0.0.12）。
 *
 * ## 与容器列表页的三处刻意不同
 * 1. **不轮询**：镜像变化只由本页发起的操作或别处构建引起，没有秒级漂移；
 *    每 5 秒重建表格只会打断用户正看到一半的列表。
 * 2. **搜索走服务端**：后端按仓库名 / 标签做内存过滤。前端再过滤一遍会让
 *    「我搜了没结果」与「后端说没结果」两种真相并存，规则漂移时无法判断谁对。
 * 3. **删除后必须说明被解绑的标签**：一个镜像 ID 常挂多个标签，删掉其中一个时
 *    镜像本体仍在。不说明就会出现「我明明删了，列表里另一个标签还在」的困惑。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const images = ref<ImageSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
/**
 * 每页条数来自用户偏好（设置页可改），`PAGE_SIZE_ALL`（0）表示「全部」。
 *
 * 类型是 `number` 而不是 store 的 `PageSize` 联合：后端回显的 `result.pageSize`
 * 是任意 number，写回时需要接住它。「全部」靠与 `PAGE_SIZE_ALL` 相等来判断，不靠类型。
 */
const pageSize = ref<number>(usePreferencesStore().pageSize)
const search = ref('')
const loading = ref(false)
const error = ref<string | null>(null)
/** 正在删除的镜像 id：让对应按钮转圈，而不是整页转圈 */
const busyIds = ref<Set<string>>(new Set())
const pullDialogVisible = ref(false)

/** 当前排序列；`null` = 不排序，后端保持守护进程返回顺序（创建时间降序） */
const sortField = ref<ImageSortField | null>(null)
const sortOrder = ref<'asc' | 'desc'>('desc')

/** 批量删除选中的镜像 */
const selected = ref<ImageSummary[]>([])
const batchBusy = ref(false)

/** 表格实例：只有 `clearSelection()` 用得到 */
const tableRef = ref<TableInstance | null>(null)

// 命令面板 / 快捷键的「拉取镜像」经动作总线转到这里（对话框开关仍归本页所有）
onActionRequest('pullImage', () => {
  pullDialogVisible.value = true
})

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
const isEmpty = computed(() => !loading.value && error.value === null && images.value.length === 0)

/** 查询参数（`load` 与搜索共用）：排序字段省略时后端不重排。 */
function listParams() {
  return {
    search: search.value.trim(),
    pageIndex: pageIndex.value,
    // ⚠️ 「全部」必须在这里换算成真实条数再发出去：0 传出去会被后端
    // Math.Clamp(pageSize, 1, 100) 夹成 1，用户选了"全部"却只看到一行。
    // 换算规则（含上限夹紧）收在 resolvePageSize 里，与分页组件的口径共用一份。
    pageSize: resolvePageSize(pageSize.value, total.value),
    ...(sortField.value ? { sortBy: sortField.value, sortDir: sortOrder.value } : {})
  }
}

async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true
  try {
    const result = await listImages(hostId.value, listParams())

    images.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100
    pageIndex.value = result.pageIndex
    // 「全部」不回写：回显的是换算后的真实条数（如 57），写回去就把「全部」降级成「57 条/页」，
    // 之后新增到 60 个镜像就再也看不到全部了——而用户当初要的就是"全都列出来"。
    if (pageSize.value !== PAGE_SIZE_ALL) pageSize.value = result.pageSize
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

/** 搜索 / 清空搜索：筛条件变了还留在第 3 页，结果一定是空的——回到第一页。 */
async function searchNow(): Promise<void> {
  pageIndex.value = 0
  await load()
}

function markBusy(id: string, busy: boolean): void {
  const next = new Set(busyIds.value)
  if (busy) next.add(id)
  else next.delete(id)
  busyIds.value = next
}

/**
 * 删除镜像。
 *
 * 被容器引用的镜像需要 `force`，而 force 会连带影响正在使用它的容器。
 * 因此默认不勾选强制，并把「有几个容器在用」写进确认框——
 * 看不到这个数字时，用户很难判断自己是不是在删一个正在被用的镜像。
 */
async function onRemove(image: ImageSummary): Promise<void> {
  const reference = primaryImageName(image.repoTags)
  const inUse = image.containerCount > 0

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('image.confirm.remove.message', { name: reference })),
        inUse
          ? h(
              'span',
              { class: 'text-status-warn text-xs' },
              t('image.confirm.remove.inUse', { count: image.containerCount })
            )
          : null,
        h('span', { class: 'text-content-muted text-xs' }, t('image.confirm.remove.tags'))
      ]),
      t('image.confirm.remove.title'),
      {
        confirmButtonText: inUse ? t('image.confirm.remove.force') : t('common.confirm'),
        cancelButtonText: t('common.cancel'),
        type: inUse ? 'warning' : 'info'
      }
    )
  } catch {
    // 用户取消：不是错误，安静退出
    return
  }

  markBusy(image.id, true)
  try {
    const result = await removeImage(
      hostId.value,
      isDangling(image.repoTags) ? image.id : reference,
      inUse
    )

    // ⚠️ 必须区分「只解绑了标签」与「真的删掉了」：
    // 只说「删除成功」，用户回到列表发现镜像还在（只是标签换了）会以为操作失败。
    if (result.deleted.length === 0 && result.untagged.length > 0) {
      ElMessage({
        type: 'info',
        message: t('image.remove.untaggedOnly', { tags: result.untagged.join('、') }),
        showClose: true
      })
    } else {
      ElMessage({ type: 'success', message: t('image.remove.done', { name: reference }) })
    }
  } finally {
    await load()
    markBusy(image.id, false)
  }
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
 * 若这里也 load()，一次改页大小就会打两遍请求。真正的查询统一由 `goToPage(0)` 触发。
 * 理由见 ListPagination 的 onSizeChange。
 */
function onPageSizeChange(size: number): void {
  usePreferencesStore().setPageSize(size)
  pageSize.value = size
}

/**
 * 表头排序 → 后端 `sortBy` 的映射。
 *
 * 与容器页同构：`prop` 是展示用的列名，不能直接当接口字段传，
 * 否则改接口字段名就要连带改模板字符串。
 */
const SORT_PROPS: Record<string, ImageSortField> = {
  repository: 'repository',
  tags: 'repository',
  size: 'size',
  created: 'created',
  usedBy: 'usedBy'
}

async function onSortChange({ prop, order }: { prop: string | null; order: string | null }): Promise<void> {
  // `order` 为 null 表示用户点了第三次表头、取消了排序
  const field = prop ? SORT_PROPS[prop] : undefined

  sortField.value = order && field ? field : null
  sortOrder.value = order === 'ascending' ? 'asc' : 'desc'
  pageIndex.value = 0
  await load()
}

function onSelectionChange(rows: ImageSummary[]): void {
  selected.value = rows
}

function clearSelection(table: { clearSelection: () => void } | null): void {
  table?.clearSelection()
  selected.value = []
}

/**
 * 批量删除。
 *
 * 与单条删除的差别在于**汇总而非中断**：逐个 catch 后报"成功 N / 失败 M"并列出失败项。
 * 用 Promise.allSettled 而不是 all——一个 reject 会让 all 提前抛出，
 * 而其余请求还在飞，最终报出来的数字会与实际不符。
 *
 * force 的判定逐镜像来：被容器引用的要 force，其余不用。
 * 一次性对全部传 force 的话，没被引用的镜像也会走"跳过优雅处理"的路径——
 * 后果是本来能干净停止的镜像被硬删。
 */
async function batchRemove(): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  const inUse = targets.filter((image) => image.containerCount > 0)

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('image.batch.confirm.message', { count: targets.length })),
        inUse.length > 0
          ? h('span', { class: 'text-status-warn text-xs' }, t('image.batch.confirm.inUse', { count: inUse.length }))
          : null
      ]),
      t('image.batch.confirm.title'),
      {
        confirmButtonText: t('image.batch.confirm.submit'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  batchBusy.value = true
  const failed: string[] = []
  const untaggedOnly: string[] = []

  try {
    await Promise.allSettled(
      targets.map(async (image) => {
        try {
          const result = await removeImage(
            hostId.value,
            isDangling(image.repoTags) ? image.id : primaryImageName(image.repoTags),
            image.containerCount > 0
          )

          // 只解绑了标签、镜像本体还在——与单条删除同一口径，不能报成"删除成功"
          if (result.deleted.length === 0 && result.untagged.length > 0) {
            untaggedOnly.push(...result.untagged)
          }
        } catch {
          failed.push(primaryImageName(image.repoTags))
        }
      })
    )

    const done = targets.length - failed.length
    if (failed.length > 0) {
      ElMessage({
        type: 'warning',
        message: t('image.batch.partial', { done, failed: failed.length, names: failed.join('、') }),
        showClose: true
      })
    } else {
      ElMessage({ type: 'success', message: t('image.batch.done', { count: done }) })
    }

    if (untaggedOnly.length > 0) {
      ElMessage({
        type: 'info',
        message: t('image.batch.untaggedOnly', { tags: untaggedOnly.join('、') }),
        showClose: true
      })
    }
  } finally {
    await load()
    batchBusy.value = false
  }
}

async function onPulled(): Promise<void> {
  pullDialogVisible.value = false
  pageIndex.value = 0
  await load()
}

function openInspect(image: ImageSummary): void {
  // 传**原始** id：路由负责段内编码，`route.params.id` 读回来时已解码。
  // 自己再 `encodeURIComponent` 一次会被路由二次编码（`sha256:` → `sha256%253A`），
  // 于是详情页拿到的 id 永远对不上。
  void router.push({
    name: 'host-image-inspect',
    params: { hostId: hostId.value, id: image.id }
  })
}

watch(
  hostId,
  (id) => {
    if (!id) return

    search.value = ''
    pageIndex.value = 0
    error.value = null
    // 换主机等于换了一批完全不同的行：不清选择的话，批量删除会作用到上一台主机的镜像上
    selected.value = []
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-image"
      :title="t('nav.images')"
      :subtitle="t('page.images.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button type="primary" @click="pullDialogVisible = true">
          {{ t('image.action.pull') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <el-input
          v-model="search"
          class="!w-40 sm:!w-64"
          clearable
          :placeholder="t('image.search.placeholder')"
          @keyup.enter="searchNow"
          @clear="searchNow"
        >
          <template #prefix><span class="i-carbon-search" /></template>
        </el-input>
        <el-button :disabled="loading" @click="searchNow">{{ t('common.search') }}</el-button>
        <span class="text-xs text-content-muted">{{ t('image.total', { total }) }}</span>
      </template>

      <!-- 错误态给整块内容位：镜像页是「要看清单」的场景，弹完就没的提示等于没说 -->
      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <!-- 批量条与表格同属"正常态"，必须包在同一个 v-else 里：
           v-else 只认**紧邻的**上一个元素，中间插一个 BatchActionBar 会直接报编译错。 -->
      <template v-else>
        <BatchActionBar :count="selected.length" :busy="batchBusy" @clear="clearSelection(tableRef)">
          <template #default>
            <el-button size="small" type="danger" plain :disabled="batchBusy" @click="batchRemove">
              {{ t('common.delete') }}
            </el-button>
          </template>
        </BatchActionBar>

        <el-table
          ref="tableRef"
          v-loading="loading"
          :data="images"
          :row-key="(row: ImageSummary) => row.id"
          empty-text=""
          @selection-change="onSelectionChange"
          @sort-change="onSortChange"
        >
          <el-table-column type="selection" width="44" reserve-selection />

          <el-table-column prop="repository" :label="t('image.col.repository')" min-width="240" sortable="custom">
          <template #default="{ row }">
            <div class="min-w-0">
              <div class="flex items-center gap-2">
                <!-- 同容器页：名称即详情入口，必须用 `<button>` 才可 Tab / 可读屏 -->
                <button
                  type="button"
                  class="app-truncate text-left font-medium hover:text-brand-500 hover:underline"
                  :class="isDangling(row.repoTags) ? 'text-content-muted italic' : 'text-content-strong'"
                  @click="openInspect(row)"
                >
                  {{ primaryImageName(row.repoTags) }}
                </button>
                <span
                  v-if="stackedTagCount(row.repoTags) > 0"
                  class="shrink-0 text-xs text-content-muted"
                >
                  {{ t('image.tags.more', { count: stackedTagCount(row.repoTags) }) }}
                </span>
              </div>
              <div class="app-truncate font-mono text-xs text-content-faint">
                {{ shortImageId(row.id) }}
              </div>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('image.col.tags')" min-width="200">
          <template #default="{ row }">
            <span v-if="isDangling(row.repoTags)" class="text-xs text-content-faint">
              {{ t('image.dangling.hint') }}
            </span>
            <span v-else class="app-truncate text-xs text-content-muted">
              {{ row.repoTags.join(' · ') }}
            </span>
          </template>
        </el-table-column>

          <el-table-column prop="size" :label="t('image.col.size')" width="150" sortable="custom">
            <template #default="{ row }">
              <!-- 独占层体积 / 逻辑总体积：两者不等时组件会自动加提示，避免高估可释放空间。 -->
              <ImageSize :size="row.size - row.sharedSize" :virtual-size="row.size" />
            </template>
          </el-table-column>

          <el-table-column prop="created" :label="t('image.col.created')" width="170" sortable="custom">
            <template #default="{ row }">
              <span class="text-xs text-content-muted">{{ formatDateTime(row.created) }}</span>
            </template>
          </el-table-column>

          <el-table-column prop="usedBy" :label="t('image.col.usedBy')" width="110" sortable="custom">
            <template #default="{ row }">
              <span
                v-if="row.containerCount > 0"
                class="text-xs text-status-ok"
              >
                {{ t('image.usedBy', { count: row.containerCount }) }}
              </span>
              <span v-else class="text-xs text-content-faint">—</span>
            </template>
          </el-table-column>

          <!-- 与容器列表共用 IconActionButton：图标按钮 + tooltip 文字标签。
             `gap-0` + 64px 定宽：两个 24px 按钮 = 48px，gap-0.5 时是 50px。 -->
          <el-table-column :label="t('container.col.actions')" width="64" align="right">
            <template #default="{ row }">
              <div class="flex items-center justify-end">
                <IconActionButton
                  icon="i-carbon-document-view"
                  :label="t('container.action.detail')"
                  @click="openInspect(row)"
                />
                <IconActionButton
                  icon="i-carbon-trash-can"
                  :label="t('common.delete')"
                  tone="danger"
                  :loading="busyIds.has(row.id)"
                  @click="onRemove(row)"
                />
              </div>
            </template>
          </el-table-column>
        </el-table>

        <EmptyState
          v-if="isEmpty"
          icon="i-carbon-image"
          :description="search ? t('image.empty.filtered') : t('image.empty.title')"
        >
          <el-button v-if="!search" type="primary" @click="pullDialogVisible = true">
            {{ t('image.action.pull') }}
          </el-button>
        </EmptyState>

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

    <PullImageDialog v-model="pullDialogVisible" :host-id="hostId" @pulled="onPulled" />

    <p class="mt-3 text-xs text-content-faint">
      {{ t('image.dangling.note', { label: DANGLING_IMAGE_LABEL }) }}
    </p>
  </div>
</template>
