<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import PullImageDialog from '@/components/business/PullImageDialog.vue'
import { onActionRequest } from '@/composables/useActionBus'
import { listImages, removeImage } from '@/api/image'
import { ApiRequestError } from '@/api'
import { formatBytes } from '@/utils/container-status'
import {
  DANGLING_IMAGE_LABEL,
  isDangling,
  primaryImageName,
  shortImageId,
  stackedTagCount
} from '@/utils/image-display'
import { formatDateTime } from '@/utils/datetime'
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
const pageSize = ref(20)
const search = ref('')
const loading = ref(false)
const error = ref<string | null>(null)
/** 正在删除的镜像 id：让对应按钮转圈，而不是整页转圈 */
const busyIds = ref<Set<string>>(new Set())
const pullDialogVisible = ref(false)

// 命令面板 / 快捷键的「拉取镜像」经动作总线转到这里（对话框开关仍归本页所有）
onActionRequest('pullImage', () => {
  pullDialogVisible.value = true
})

/** URL 上的 hostId。**以它为准**而不是 store.currentId：深链进入时 store 可能还没被守卫写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const isEmpty = computed(() => !loading.value && error.value === null && images.value.length === 0)

const byteUnits = computed(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true
  try {
    const result = await listImages(hostId.value, {
      search: search.value.trim(),
      pageIndex: pageIndex.value,
      pageSize: pageSize.value
    })

    images.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100
    pageIndex.value = result.pageIndex
    pageSize.value = result.pageSize
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

      <el-table
        v-else
        v-loading="loading"
        :data="images"
        :row-key="(row: ImageSummary) => row.id"
        empty-text=""
      >
        <el-table-column :label="t('image.col.repository')" min-width="220">
          <template #default="{ row }">
            <div class="min-w-0">
              <div class="flex items-center gap-2">
                <span
                  class="app-truncate font-medium"
                  :class="
                    isDangling(row.repoTags) ? 'text-content-muted italic' : 'text-content-strong'
                  "
                >
                  {{ primaryImageName(row.repoTags) }}
                </span>
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

        <el-table-column :label="t('image.col.tags')" min-width="170">
          <template #default="{ row }">
            <span v-if="isDangling(row.repoTags)" class="text-xs text-content-faint">
              {{ t('image.dangling.hint') }}
            </span>
            <span v-else class="app-truncate text-xs text-content-muted">
              {{ row.repoTags.join(' · ') }}
            </span>
          </template>
        </el-table-column>

        <el-table-column :label="t('image.col.size')" width="110">
          <template #default="{ row }">
            <span class="text-sm tabular-nums">{{ formatBytes(row.size, byteUnits) }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('image.col.created')" width="170">
          <template #default="{ row }">
            <span class="text-xs text-content-muted">{{ formatDateTime(row.created) }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('image.col.usedBy')" width="110">
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

        <!-- 与容器列表共用 IconActionButton：图标按钮 + tooltip 文字标签 -->
        <el-table-column :label="t('container.col.actions')" width="100" align="right">
          <template #default="{ row }">
            <div class="flex items-center justify-end gap-0.5">
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

      <div v-if="!error && pageCount > 1" class="flex items-center justify-end gap-2 px-4 py-3">
        <el-button size="small" :disabled="pageIndex === 0" @click="goToPage(pageIndex - 1)">
          {{ t('common.prev') }}
        </el-button>
        <span class="text-xs text-content-muted">{{ pageIndex + 1 }} / {{ pageCount }}</span>
        <el-button
          size="small"
          :disabled="pageIndex + 1 >= pageCount"
          @click="goToPage(pageIndex + 1)"
        >
          {{ t('common.next') }}
        </el-button>
      </div>
    </SectionCard>

    <PullImageDialog v-model="pullDialogVisible" :host-id="hostId" @pulled="onPulled" />

    <p class="mt-3 text-xs text-content-faint">
      {{ t('image.dangling.note', { label: DANGLING_IMAGE_LABEL }) }}
    </p>
  </div>
</template>
