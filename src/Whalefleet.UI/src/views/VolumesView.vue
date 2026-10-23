<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import type { TableInstance } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import ListPagination from '@/components/common/ListPagination.vue'
import ImageSize from '@/components/business/ImageSize.vue'
import BatchActionBar from '@/components/common/BatchActionBar.vue'
import PruneDialog from '@/components/business/PruneDialog.vue'
import {
  batchRemoveVolumes,
  createVolume,
  inspectVolume,
  listVolumes,
  removeVolume
} from '@/api/volume'
import { ApiRequestError } from '@/api'
import {
  hasVolumeRefCount,
  hasVolumeUsage,
  isValidResourceName,
  isVolumeInUse
} from '@/utils/resource-display'
import { formatDateTime } from '@/utils/datetime'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import { PAGE_SIZE_ALL, resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import type { BatchOperationFailure, VolumeDetail, VolumeSummary } from '@/types/api'

/**
 * 数据卷列表页（0.2.0，分页于 0.14.x 补齐）。
 *
 * ## 三处与镜像页刻意不同
 * 1. **分页与容器 / 镜像同一套口径**：服务端内存切片，`pageIndex` 从 0 开始，
 *    每页条数来自用户偏好、上限 100（超出由后端夹紧并回显）。
 * 2. **按名字操作，没有 ID**：数据卷用名字标识。别照搬镜像那套 ID 解析。
 * 3. **`size` / `refCount` 为 `null` 显示「—」，为 `0` 如实显示**：这两个字段可能
 *    因守护进程不报用量而缺失（`null`），但 `0` 是一个确切的答案（见 `hasVolumeUsage`）。
 */
const { t } = useI18n()
const route = useRoute()

const volumes = ref<VolumeSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
/**
 * 每页条数来自用户偏好（设置页可改），`PAGE_SIZE_ALL`（0）表示「全部」。
 *
 * 类型是 `number` 而不是 store 的 `PageSize` 联合：后端回显的 `result.pageSize`
 * 是任意 number，写回时需要接住它。「全部」靠与 `PAGE_SIZE_ALL` 相等来判断，不靠类型。
 */
const pageSize = ref<number>(usePreferencesStore().pageSize)
const warnings = ref<string[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
/** 正在删除的卷名：让对应按钮转圈，而不是整页转圈 */
const busyNames = ref<Set<string>>(new Set())

/** 批量删除选中的卷 */
const selected = ref<VolumeSummary[]>([])
const batchBusy = ref(false)
const tableRef = ref<TableInstance | null>(null)

/** 创建对话框 */
const createVisible = ref(false)

/** 清理对话框（0.7.0）：清理未被引用的数据卷，不可逆。 */
const pruneDialogVisible = ref(false)
const createBusy = ref(false)
const createFormRef = ref<FormInstance>()
const createForm = ref({ name: '', driver: '', labels: '' })

/** 详情抽屉 */
const detailVisible = ref(false)
const detail = ref<VolumeDetail | null>(null)
const detailBusy = ref(false)

/** URL 上的 hostId。**以它为准**而不是 store.currentId：深链进入时 store 可能还没被守卫写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

const isEmpty = computed(() => !loading.value && error.value === null && volumes.value.length === 0)

/**
 * 总页数。
 *
 * 必须用 `resolvePageSize` 而不是直接 `total / pageSize`：
 * `pageSize` 可能是 `PAGE_SIZE_ALL`（0，除以它得 Infinity），也可能被上限夹紧。
 * 与分页组件口径不一致会出现「分页条显示了第 2 页按钮，但 goToPage 拦下它」——按钮点不动。
 */
const pageCount = computed(() =>
  Math.max(1, Math.ceil(total.value / resolvePageSize(pageSize.value, total.value)))
)

/**
 * 列表查询参数。
 *
 * ⚠️ 「全部」必须在这里换算成真实条数再发出去：0 传出去会被后端
 * `Math.Clamp(pageSize, 1, 100)` 夹成 1，用户选了「全部」却只看到一行。
 */
function listParams() {
  return {
    pageIndex: pageIndex.value,
    pageSize: resolvePageSize(pageSize.value, total.value)
  }
}

/**
 * 创建表单的校验规则。
 *
 * 名字那条刻意返回自定义文案而不是纯 true/false：非法的形态有好几种
 * （以连字符开头、有空格、有斜杠），一句「格式不对」会让用户猜。
 * ⚠️ 前端校验只是为了**即时反馈**，后端那道永远在。
 */
const createRules = computed<FormRules>(() => ({
  name: [
    { required: true, message: t('volume.form.nameRequired'), trigger: 'blur' },
    {
      validator: (_rule, value: string, callback) => {
        callback(
          isValidResourceName(String(value ?? '').trim())
            ? undefined
            : new Error(t('volume.form.nameInvalid'))
        )
      },
      trigger: 'blur'
    }
  ]
}))

async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true
  try {
    const result = await listVolumes(hostId.value, listParams())

    // 删光当前页（如把最后一页的最后一个卷删了）时自动回退一页：
    // 否则用户会停在一片空白上，且「共 N 个」与空表同时显示，很容易被读成「全删完了」。
    if (result.items.length === 0 && result.pageIndex > 0 && result.total > 0) {
      pageIndex.value = result.pageIndex - 1
      await load()
      return
    }

    volumes.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100，
    // 拿请求里的值去算「还有没有下一页」会得出错误结论。
    pageIndex.value = result.pageIndex
    // 「全部」不回写：回显的是换算后的真实条数（如 57），写回去就把「全部」降级成「57 条/页」。
    if (pageSize.value !== PAGE_SIZE_ALL) pageSize.value = result.pageSize

    // ⚠️ 必须回带 warnings：守护进程会用它说「某个卷的驱动没装」。
    // 丢掉的话用户只看到列表少了几个卷，却不知道为什么。
    warnings.value = result.warnings ?? []
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : t('volume.loadFailed')
  } finally {
    loading.value = false
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
 * ⚠️ 这里**只更新本地状态、不发请求**：`ListPagination` 在改页大小时会同时抛
 * `update:pageSize` 与 `update:pageIndex`(0)，两个事件各接一个处理器。
 * 若这里也 load()，一次改页大小就会打两遍请求。真正的查询统一由 `goToPage(0)` 触发。
 */
function onPageSizeChange(size: number): void {
  usePreferencesStore().setPageSize(size)
  pageSize.value = size
}

function openCreate(): void {
  createForm.value = { name: '', driver: '', labels: '' }
  createVisible.value = true
}

/** 把「每行一条 key=value」的文本解析成标签字典（无法解析的行静默跳过）。 */
function parseLabels(text: string): Record<string, string> {
  const result: Record<string, string> = {}

  for (const line of text.split('\n')) {
    const trimmed = line.trim()
    if (trimmed.length === 0) continue

    const separator = trimmed.indexOf('=')
    if (separator <= 0) continue

    result[trimmed.slice(0, separator).trim()] = trimmed.slice(separator + 1).trim()
  }

  return result
}

async function submitCreate(): Promise<void> {
  const valid = await createFormRef.value?.validate().catch(() => false)
  if (valid === false) return

  createBusy.value = true
  try {
    const driver = createForm.value.driver.trim()
    const labels = parseLabels(createForm.value.labels)

    const created = await createVolume(hostId.value, {
      name: createForm.value.name.trim(),
      ...(driver.length > 0 ? { driver } : {}),
      ...(Object.keys(labels).length > 0 ? { labels } : {})
    })

    createVisible.value = false

    // ⚠️ 必须刷新列表：Docker 的同名创建**不报错**，而是返回已存在的那一个。
    // 不刷新的话用户会以为自己凭空造出了一个卷。
    await load()

    ElMessage({ type: 'success', message: t('volume.created', { name: created.name }) })
  } catch (e) {
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    createBusy.value = false
  }
}

/**
 * 删除一个卷。
 *
 * ⚠️ **`force` 不在这里出现**：实测它删不掉仍被引用的卷——
 * 只要还有容器挂着，守护进程就拒绝，带不带 force 都一样。
 * 二次确认因此说的是「先删掉使用它的容器」，而不是「勾选强制」。
 * `force` 只在「卷已经不存在」时有用（幂等重试），那条路径由批量删除承担。
 */
async function removeOne(volume: VolumeSummary): Promise<void> {
  const inUse = isVolumeInUse(volume)

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('volume.remove.confirm.message', { name: volume.name })),
        inUse
          ? h(
              'span',
              { class: 'text-status-warn text-xs' },
              t('volume.remove.confirm.inUse', { count: volume.refCount ?? 0 })
            )
          : null
      ]),
      t('volume.remove.confirm.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  busyNames.value = new Set(busyNames.value).add(volume.name)

  try {
    await removeVolume(hostId.value, volume.name)
    await load()
    ElMessage({ type: 'success', message: t('volume.removed', { name: volume.name }) })
  } catch (e) {
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    const next = new Set(busyNames.value)
    next.delete(volume.name)
    busyNames.value = next
  }
}

/**
 * 批量删除（走服务端 `volumes/batch-remove`）。
 *
 * 与单条删除的差别在于**汇总而非中断**：服务端逐条删、逐条汇报，
 * 界面把 `failures` 列出来——中途报错会让用户既不知道删了几个，也不敢重试。
 *
 * 这里传 `force=true`：它的作用是「卷已经不在了也算成功」，
 * 这样上一轮已经删掉的项再提交一次不会让整批报错。
 */
async function batchRemove(): Promise<void> {
  const targets = [...selected.value]
  if (targets.length === 0) return

  const hasInUse = targets.some((volume) => isVolumeInUse(volume))

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('volume.batch.confirm.message', { count: targets.length })),
        hasInUse
          ? h('span', { class: 'text-status-warn text-xs' }, t('volume.batch.confirm.inUse'))
          : null
      ]),
      t('volume.batch.confirm.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  batchBusy.value = true

  const failures: BatchOperationFailure[] = []

  try {
    const result = await batchRemoveVolumes(
      hostId.value,
      targets.map((volume) => volume.name),
      true
    )

    failures.push(...result.failures)

    if (failures.length > 0) {
      ElMessage({
        type: 'warning',
        message: t('volume.batch.partial', {
          done: result.succeeded,
          failed: failures.length,
          names: failures.map((failure) => failure.resourceId).join('、')
        }),
        showClose: true
      })
    } else {
      ElMessage({ type: 'success', message: t('volume.batch.done', { count: result.succeeded }) })
    }

    await load()
  } catch (e) {
    // 只有入参非法（1000）与主机不可达（1301/1302）会走到这里
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    batchBusy.value = false
    clearSelection(tableRef)
  }
}

async function openDetail(volume: VolumeSummary): Promise<void> {
  detailVisible.value = true
  detailBusy.value = true
  detail.value = null

  try {
    detail.value = await inspectVolume(hostId.value, volume.name)
  } catch (e) {
    detailVisible.value = false
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    detailBusy.value = false
  }
}

function onSelectionChange(rows: VolumeSummary[]): void {
  selected.value = rows
}

function clearSelection(table: { clearSelection: () => void } | null): void {
  table?.clearSelection()
  selected.value = []
}

watch(
  () => route.params.hostId,
  () => {
    warnings.value = []
    error.value = null
    // 换主机等于换了一批完全不同的行：不清选择的话，批量删除会作用到上一台主机的卷上
    selected.value = []
    // 页码也必须归零：停在上一台主机的第 3 页，换过去很可能是一片空白
    pageIndex.value = 0
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-data-volume"
      :title="t('nav.volumes')"
      :title-tip="t('page.volumes.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <!-- 清理是不可逆操作，因此单独一个按钮而不是挂在行内操作里 -->
        <el-button @click="pruneDialogVisible = true">
          {{ t('prune.action.volumes') }}
        </el-button>
        <el-button type="primary" @click="openCreate">{{ t('volume.action.create') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">
          {{ t('volume.total', { total }) }}
        </span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <template v-else>
        <!-- 守护进程的告警必须显眼：它是「列表少了几个卷」的唯一解释 -->
        <div v-if="warnings.length > 0" class="px-4 pt-3">
          <el-alert type="warning" :closable="false" show-icon>
            <template #title>{{ t('volume.warnings.title') }}</template>
            <ul class="mt-1 list-disc pl-4 text-xs">
              <li v-for="(warning, index) in warnings" :key="index">{{ warning }}</li>
            </ul>
          </el-alert>
        </div>

        <BatchActionBar :count="selected.length" :busy="batchBusy" @clear="clearSelection(tableRef)">
          <template #default>
            <el-button size="small" type="danger" plain :disabled="batchBusy" @click="batchRemove">
              {{ t('common.delete') }}
            </el-button>
          </template>
        </BatchActionBar>

        <EmptyState
          v-if="isEmpty"
          icon="i-carbon-data-volume"
          :title="t('volume.empty')"
          :description="t('volume.emptyHint')"
        />

        <el-table
          v-else
          ref="tableRef"
          v-loading="loading"
          :data="volumes"
          :row-key="(row: VolumeSummary) => row.name"
          empty-text=""
          @selection-change="onSelectionChange"
        >
          <el-table-column type="selection" width="44" reserve-selection />

          <el-table-column :label="t('volume.col.name')" min-width="220">
            <template #default="{ row }">
              <!--
                名称即详情入口，用 `<button>` 才可 Tab / 可读屏。
                ⚠️ 悬浮提示**始终**给：卷名可长可短，提示在不在与内容长短无关，
                读者才能把它当成可靠的入口。
              -->
              <el-tooltip :content="row.name" placement="top-start" :show-after="TIP_DELAY_MS">
                <button
                  type="button"
                  class="app-truncate block text-left font-medium text-content-strong hover:text-brand-500 hover:underline"
                  @click="openDetail(row)"
                >
                  {{ row.name }}
                </button>
              </el-tooltip>
              <el-tooltip
                v-if="row.mountpoint"
                :content="row.mountpoint"
                placement="top-start"
                :show-after="TIP_DELAY_MS"
              >
                <div class="app-truncate font-mono text-xs text-content-faint">
                  {{ row.mountpoint }}
                </div>
              </el-tooltip>
              <div v-else class="app-truncate font-mono text-xs text-content-faint">—</div>
            </template>
          </el-table-column>

          <el-table-column :label="t('volume.col.driver')" width="120">
            <template #default="{ row }">
              <span class="text-xs">{{ row.driver }}</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('volume.col.size')" width="130">
            <template #default="{ row }">
              <!-- null 才是「不知道」：显示「—」而不是「0 B」；真实的 0 照常显示 -->
              <ImageSize v-if="hasVolumeUsage(row)" :size="row.size" />
              <span v-else class="text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('volume.col.refCount')" width="110">
            <template #default="{ row }">
              <span v-if="hasVolumeRefCount(row)" class="app-tabular">{{ row.refCount }}</span>
              <span v-else class="text-content-faint">—</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('volume.col.created')" width="170">
            <template #default="{ row }">
              <span class="text-xs text-content-muted">
                {{ row.created ? formatDateTime(row.created, t) : '—' }}
              </span>
            </template>
          </el-table-column>

          <el-table-column :label="t('common.actions')" width="96" align="right">
            <template #default="{ row }">
              <IconActionButton
                icon="i-carbon-information"
                :title="t('volume.action.detail')"
                @click="openDetail(row)"
              />
              <IconActionButton
                icon="i-carbon-trash-can"
                :title="t('volume.action.remove')"
                danger
                :loading="busyNames.has(row.name)"
                @click="removeOne(row)"
              />
            </template>
          </el-table-column>
        </el-table>

        <!-- 分页条始终显示：翻到最后一页删光后仍能回到上一页，而不是对着一片空白猜。 -->
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

    <!-- 创建对话框 -->
    <el-dialog v-model="createVisible" :title="t('volume.form.title')" width="480px">
      <el-form ref="createFormRef" :model="createForm" :rules="createRules" label-width="96px">
        <el-form-item :label="t('volume.form.name')" prop="name">
          <el-input v-model="createForm.name" :placeholder="t('volume.form.namePlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('volume.form.driver')">
          <el-input v-model="createForm.driver" :placeholder="t('volume.form.driverPlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('volume.form.labels')">
          <el-input
            v-model="createForm.labels"
            type="textarea"
            :rows="3"
            :placeholder="t('volume.form.labelsPlaceholder')"
          />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createVisible = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="createBusy" @click="submitCreate">
          {{ t('common.create') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 详情抽屉 -->
    <el-drawer v-model="detailVisible" :title="detail?.name ?? t('volume.detail.title')" size="480px">
      <div v-loading="detailBusy">
        <el-descriptions v-if="detail" :column="1" border>
          <el-descriptions-item :label="t('volume.col.driver')">
            {{ detail.driver }}
          </el-descriptions-item>
          <el-descriptions-item :label="t('volume.detail.mountpoint')">
            <span class="font-mono text-xs">{{ detail.mountpoint || '—' }}</span>
          </el-descriptions-item>
          <el-descriptions-item :label="t('volume.detail.scope')">
            {{ detail.scope }}
          </el-descriptions-item>
          <el-descriptions-item :label="t('volume.col.created')">
            {{ detail.created ? formatDateTime(detail.created, t) : '—' }}
          </el-descriptions-item>
        </el-descriptions>

        <div v-if="detail && Object.keys(detail.options).length > 0" class="mt-4">
          <p class="mb-2 text-sm font-medium">{{ t('volume.detail.options') }}</p>
          <el-descriptions :column="1" border>
            <el-descriptions-item v-for="(value, key) in detail.options" :key="key" :label="key">
              <span class="font-mono text-xs">{{ value }}</span>
            </el-descriptions-item>
          </el-descriptions>
        </div>

        <div v-if="detail && Object.keys(detail.labels).length > 0" class="mt-4">
          <p class="mb-2 text-sm font-medium">{{ t('volume.detail.labels') }}</p>
          <div class="flex flex-wrap gap-2">
            <el-tag v-for="(value, key) in detail.labels" :key="key" size="small" type="info">
              {{ key }}={{ value }}
            </el-tag>
          </div>
        </div>
      </div>
    </el-drawer>

    <PruneDialog
      v-model="pruneDialogVisible"
      :host-id="hostId"
      resource="volumes"
      @done="load"
    />
  </div>
</template>