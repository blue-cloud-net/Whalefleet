<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import EmptyState from '@/components/common/EmptyState.vue'
import { downloadContainerFile, downloadVolumeFile, listContainerFiles, listVolumeFiles, writeContainerFile, writeVolumeFile } from '@/api/transfer'
import { formatDateTime } from '@/utils/datetime'
import { saveBlob } from '@/utils/download'
import type { FileEntry, FileListResult, FileEntryKind } from '@/types/api'

/**
 * 文件浏览器（容器与卷共用）。
 *
 * ## 为什么容器与卷共用同一个组件
 * 两侧的**交互**完全一致（面包屑、下载、上传、空目录提示），差别只在
 * API 走哪条路。若各写一份，任何一处文案或行为修正都要改两遍——
 * 而两份迟早会分叉。
 *
 * ## 上行目录靠后端的 `parentPath`，不靠前端拼字符串
 * 路径规范化（去掉 `.`、统一 `\`、处理末尾斜杠）有一套规则，前端再实现一遍必然
 * 与后端不一致；不一致时症状是「点上一级跳到了别处」。后端已经把父目录算好了。
 *
 * ## 为什么列目录可能被截断
 * Docker 没有「列目录」接口，列举靠读目标路径的 tar 条目。条目极多时读完整份
 * 代价不可控，因此后端有上限——`truncated` 必须提示，否则用户会以为
 * 「目录里就这么多东西」。
 */

const props = defineProps<{
  hostId: string
  /** 浏览对象类型 */
  kind: 'container' | 'volume'
  /** 容器 ID 或卷名 */
  target: string
  /** 是否允许写入（由主机 / 全局设置决定；后端仍会兜底校验） */
  allowWrite?: boolean
}>()

const { t } = useI18n()

const listing = ref<FileListResult | null>(null)
const path = ref<string | undefined>(undefined)
const loading = ref(false)
const error = ref<string | null>(null)
/** 正在下载的条目路径：只让那一行转圈 */
const downloading = ref<string | null>(null)

const uploading = ref(false)
const uploadPercent = ref<number | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

/** 当前目录路径（后端规范化后的值）。 */
const currentPath = computed(() => listing.value?.path ?? '/')

/** 面包屑：把 `/a/b/c` 切成可点击的段。 */
const crumbs = computed(() => {
  const segments = currentPath.value.split('/').filter(Boolean)
  const items: Array<{ label: string; path: string }> = []

  let accumulated = ''

  for (const segment of segments) {
    accumulated += `/${segment}`
    items.push({ label: segment, path: accumulated })
  }

  return items
})

/** 是否有上一级可去。 */
const parentPath = computed(() => listing.value?.parentPath ?? null)

/** 目录在前、文件在后；同级按名称排序（与后端口径一致，前端只做兜底排序）。 */
const entries = computed(() => listing.value?.entries ?? [])

watch(
  () => [props.hostId, props.kind, props.target],
  () => {
    path.value = undefined
    void load()
  }
)

onMounted(() => void load())

/** 载入当前目录。 */
async function load(): Promise<void> {
  loading.value = true
  error.value = null

  try {
    listing.value =
      props.kind === 'container'
        ? await listContainerFiles(props.hostId, props.target, path.value)
        : await listVolumeFiles(props.hostId, props.target, path.value)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : t('fileBrowser.loadFailed')
    listing.value = null
  } finally {
    loading.value = false
  }
}

/** 进入指定目录。 */
function enter(target: string): void {
  path.value = target
  void load()
}

/** 打开条目：目录进入，文件下载。 */
function activate(entry: FileEntry): void {
  if (entry.kind === 'directory') {
    enter(entry.path)
    return
  }

  void download(entry)
}

/** 下载单个文件。 */
async function download(entry: FileEntry): Promise<void> {
  if (entry.kind === 'directory') {
    return
  }

  downloading.value = entry.path

  try {
    const blob =
      props.kind === 'container'
        ? await downloadContainerFile(props.hostId, props.target, entry.path)
        : await downloadVolumeFile(props.hostId, props.target, entry.path)

    saveBlob(blob, entry.name)
  } finally {
    downloading.value = null
  }
}

/** 打开系统文件选择框。 */
function pickFile(): void {
  fileInput.value?.click()
}

/** 选中文件后立即上传（同名覆盖由提示先说明，不再二次弹窗）。 */
async function onFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]

  // 清空 value：否则选中同一个文件第二次不会触发 change
  input.value = ''

  if (!file) {
    return
  }

  uploading.value = true
  uploadPercent.value = 0

  try {
    if (props.kind === 'container') {
      await writeContainerFile(props.hostId, props.target, currentPath.value, file, onProgress)
    } else {
      await writeVolumeFile(props.hostId, props.target, currentPath.value, file, onProgress)
    }

    ElMessage.success(t('fileBrowser.uploadSucceeded', { name: file.name }))
    await load()
  } finally {
    uploading.value = false
    uploadPercent.value = null
  }
}

/** 上传进度回调。 */
function onProgress(percent: number | null): void {
  uploadPercent.value = percent
}

/** 条目类型文案（显式映射，不在模板里拼 i18n key）。 */
const KIND_LABELS: Record<FileEntryKind, string> = {
  directory: 'fileBrowser.kind.directory',
  file: 'fileBrowser.kind.file',
  symlink: 'fileBrowser.kind.symlink',
  other: 'fileBrowser.kind.other'
}

/** 条目类型文案。 */
function kindLabel(kind: FileEntryKind): string {
  return t(KIND_LABELS[kind] ?? 'fileBrowser.kind.other')
}

/** 条目可用的人性化大小（目录不给大小——Docker 不报，写 0 会被读成「空目录」）。 */
function sizeLabel(entry: FileEntry): string {
  if (entry.kind === 'directory') {
    return '—'
  }

  return formatBytes(entry.sizeBytes)
}

/** 字节数 → 可读大小。 */
function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let index = 0

  while (value >= 1024 && index < units.length - 1) {
    value /= 1024
    index += 1
  }

  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[index]}`
}
</script>

<template>
  <div class="flex flex-col gap-3">
    <!-- 工具条：面包屑在左（路径永远是最重要的上下文），操作在右。 -->
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div class="flex min-w-0 flex-wrap items-center gap-1 text-sm">
        <el-button
          text
          size="small"
          :class="['!px-1', { 'text-content-muted': !parentPath }]"
          :disabled="!parentPath"
          :title="t('fileBrowser.up')"
          @click="parentPath && enter(parentPath)"
        >
          <span class="i-carbon-arrow-up" aria-hidden="true" />
        </el-button>

        <button
          type="button"
          class="rounded px-1 hover:bg-surface-hover"
          :class="currentPath === '/' ? 'font-medium' : 'text-content-muted'"
          @click="enter('/')"
        >
          {{ t('fileBrowser.root') }}
        </button>

        <template v-for="crumb in crumbs" :key="crumb.path">
          <span class="text-content-muted">/</span>
          <button
            type="button"
            class="rounded px-1 hover:bg-surface-hover"
            :class="crumb.path === currentPath ? 'font-medium' : 'text-content-muted'"
            @click="enter(crumb.path)"
          >
            {{ crumb.label }}
          </button>
        </template>
      </div>

      <div class="flex items-center gap-2">
        <span v-if="uploading" class="text-xs text-content-muted">
          {{
            uploadPercent == null
              ? t('fileBrowser.uploadDialog.hint')
              : t('fileBrowser.uploadDialog.progress', { progress: uploadPercent })
          }}
        </span>

        <el-button size="small" :loading="loading" @click="load">
          {{ t('fileBrowser.refresh') }}
        </el-button>

        <el-tooltip
          v-if="allowWrite === false"
          :content="t('fileBrowser.writeDisabled')"
          placement="top"
        >
          <span>
            <el-button size="small" disabled>
              {{ t('fileBrowser.upload') }}
            </el-button>
          </span>
        </el-tooltip>
        <el-button v-else size="small" :loading="uploading" @click="pickFile">
          {{ t('fileBrowser.upload') }}
        </el-button>

        <input
          ref="fileInput"
          type="file"
          class="hidden"
          @change="onFileSelected"
        />
      </div>
    </div>

    <!-- 截断提示：必须显眼，否则用户会以为目录里就这么多东西 -->
    <el-alert
      v-if="listing?.truncated"
      type="warning"
      :closable="false"
      show-icon
      :title="t('fileBrowser.truncated')"
    />

    <el-alert
      v-if="uploading && allowWrite !== false"
      type="info"
      :closable="false"
      show-icon
      :title="t('fileBrowser.overwriteHint')"
    />

    <div v-if="error" class="rounded border border-danger/40 bg-danger/5 p-3 text-sm">
      {{ error }}
    </div>

    <el-table
      v-else
      v-loading="loading"
      :data="entries"
      size="small"
      class="w-full"
      row-key="path"
      @row-dblclick="activate"
    >
      <el-table-column :label="t('fileBrowser.col.name')" min-width="240">
        <template #default="{ row }">
          <button
            type="button"
            class="flex min-w-0 items-center gap-2 text-left"
            :title="row.path"
            @click="activate(row)"
          >
            <span
              class="shrink-0 text-base"
              :class="row.kind === 'directory' ? 'i-carbon-folder' : 'i-carbon-document'"
              aria-hidden="true"
            />
            <span class="truncate">{{ row.name }}</span>
            <span v-if="row.linkTarget" class="shrink-0 text-xs text-content-muted">
              → {{ row.linkTarget }}
            </span>
          </button>
        </template>
      </el-table-column>

      <el-table-column :label="t('fileBrowser.col.kind')" width="110">
        <template #default="{ row }">
          <span class="text-xs text-content-muted">{{ kindLabel(row.kind) }}</span>
        </template>
      </el-table-column>

      <el-table-column :label="t('fileBrowser.col.size')" width="110" align="right">
        <template #default="{ row }">
          <span class="tabular-nums text-xs">{{ sizeLabel(row) }}</span>
        </template>
      </el-table-column>

      <el-table-column :label="t('fileBrowser.col.modified')" width="180">
        <template #default="{ row }">
          <span class="text-xs text-content-muted">
            {{ row.modifiedAt ? formatDateTime(row.modifiedAt) : '—' }}
          </span>
        </template>
      </el-table-column>

      <el-table-column width="90" align="right">
        <template #default="{ row }">
          <el-button
            v-if="row.kind !== 'directory'"
            text
            size="small"
            :loading="downloading === row.path"
            :title="t('fileBrowser.download')"
            @click="download(row)"
          >
            <span class="i-carbon-download" aria-hidden="true" />
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <EmptyState compact icon="i-carbon-folder" :description="t('fileBrowser.empty')" />
      </template>
    </el-table>
  </div>
</template>
