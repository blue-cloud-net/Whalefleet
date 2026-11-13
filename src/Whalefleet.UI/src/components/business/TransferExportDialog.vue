<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import {
  exportContainer,
  exportImage,
  getContainerExportStatus,
  getImageExportStatus
} from '@/api/transfer'
import { openDownloadUrl } from '@/utils/download'
import type { TransferJobStatus } from '@/types/api'

/**
 * 导出（镜像 / 容器）进度对话框（0.14.0）。
 *
 * ## 为什么必须轮询而不是「请求返回就下载」
 * 导出是**后台任务**：`docker save` 一个几 GB 的镜像要几十秒到几分钟，
 * HTTP 请求扛不住那么久（反代会先掐断）。因此发起请求只拿到 `jobId`，
 * 产物要靠状态轮询到 `succeeded` 才存在。
 *
 * ## 为什么以轮询为权威、不依赖 SignalR
 * 实时通道可能断（反代超时、页面休眠）。若只信推送，一条丢失的消息会让用户
 * 永远停在 90%。这里的定时器是**唯一**的进度来源，推送只是额外惊喜。
 *
 * ## 关掉对话框要不要取消任务
 * **不要**。用户可能只是想先干别的；任务在服务端继续跑，产物也有 TTL。
 * 界面上写清「关掉也继续导出」，否则没人敢关。
 */

const props = defineProps<{
  modelValue: boolean
  hostId: string
  /** 导出对象类型 */
  kind: 'image' | 'container'
  /** 镜像引用 / 镜像 ID 或容器 ID */
  resourceId: string
  /** 展示用的名字（镜像名或容器名） */
  resourceName?: string
}>()

const emit = defineEmits<{ 'update:modelValue': [boolean] }>()

const { t } = useI18n()

/** 轮询间隔：导出是分钟级操作，1 秒足够且不会把后端压出压力。 */
const POLL_INTERVAL_MS = 1000

const jobId = ref<string | null>(null)
const status = ref<TransferJobStatus | null>(null)
const starting = ref(false)
const error = ref<string | null>(null)
const reused = ref(false)

let timer: ReturnType<typeof setTimeout> | null = null
/** 对话框是否已被关掉（关掉后停止轮询，但服务端任务继续） */
let disposed = false

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value)
})

/** 是否已到终态。 */
const finished = computed(
  () => status.value != null && ['succeeded', 'failed', 'canceled'].includes(status.value.status)
)

/** 进度条取值：不知道总进度时给一个「进行中」的假进度，而不是 0。 */
const percent = computed(() => status.value?.percent ?? (finished.value ? 100 : 35))

watch(
  () => props.modelValue,
  (open) => {
    if (open) {
      void start()
    } else {
      stopPolling()
    }
  },
  { immediate: true }
)

onBeforeUnmount(() => {
  disposed = true
  stopPolling()
})

/** 发起导出。 */
async function start(): Promise<void> {
  if (!props.resourceId) {
    return
  }

  starting.value = true
  error.value = null
  status.value = null
  reused.value = false

  try {
    const accepted =
      props.kind === 'image'
        ? await exportImage(props.hostId, props.resourceId)
        : await exportContainer(props.hostId, props.resourceId)

    jobId.value = accepted.jobId
    reused.value = accepted.reused
    schedulePoll()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    starting.value = false
  }
}

/** 排下一次轮询。 */
function schedulePoll(): void {
  stopPolling()
  timer = setTimeout(() => void poll(), POLL_INTERVAL_MS)
}

/** 停掉轮询。 */
function stopPolling(): void {
  if (timer != null) {
    clearTimeout(timer)
    timer = null
  }
}

/** 查询一次状态。 */
async function poll(): Promise<void> {
  if (disposed || !jobId.value) {
    return
  }

  try {
    status.value =
      props.kind === 'image'
        ? await getImageExportStatus(props.hostId, jobId.value)
        : await getContainerExportStatus(props.hostId, jobId.value)
  } catch (cause) {
    // 单次失败不终止：网络抖动很常见，下一次轮询通常就好了
    error.value = cause instanceof Error ? cause.message : String(cause)
  }

  if (finished.value) {
    stopPolling()

    return
  }

  schedulePoll()
}

/** 下载产物。 */
function download(): void {
  if (!status.value?.downloadUrl) {
    return
  }

  openDownloadUrl(status.value.downloadUrl)
  ElMessage.success(t('transfer.downloadStarted'))
}

/** 再导一次（失败后重试用）。 */
function retry(): void {
  void start()
}
</script>

<template>
  <el-dialog v-model="visible" :title="t('transfer.export.title')" width="520px">
    <div class="flex flex-col gap-3">
      <p class="text-sm text-content-muted">
        {{ resourceName ?? resourceId }}
      </p>

      <p class="text-xs text-content-muted">{{ t('transfer.export.backgroundHint') }}</p>

      <div v-if="error" class="rounded border border-danger/40 bg-danger/5 p-3 text-sm">
        {{ error }}
      </div>

      <div v-else class="flex flex-col gap-2">
        <el-progress
          :percentage="percent"
          :status="status?.status === 'failed' ? 'exception' : finished ? 'success' : undefined"
        />

        <p class="text-xs text-content-muted">
          {{ status?.step ?? t('transfer.export.preparing') }}
        </p>

        <!-- 复用了正在跑的任务：这不是错误，但要让用户知道没点出两个任务 -->
        <p v-if="reused" class="text-xs text-content-muted">
          {{ t('transfer.export.reused') }}
        </p>

        <p v-if="status?.errorMessage" class="text-xs text-danger">
          {{ status.errorMessage }}
        </p>
      </div>
    </div>

    <template #footer>
      <el-button @click="visible = false">{{ t('common.close') }}</el-button>
      <el-button v-if="status?.status === 'failed'" @click="retry">
        {{ t('common.retry') }}
      </el-button>
      <el-button
        type="primary"
        :loading="starting"
        :disabled="!status?.downloadUrl"
        @click="download"
      >
        {{ t('transfer.download') }}
      </el-button>
    </template>
  </el-dialog>
</template>
