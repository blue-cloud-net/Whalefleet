<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import type { HostOverview } from '@/types/api'
import { hostTone, HOST_TONE_CHIP, HOST_TONE_LABEL_KEY } from '@/utils/host-status'
import StatusDot from '@/components/common/StatusDot.vue'

/**
 * 主机落地页的头部：主机身份 + 连接端点 + 刷新。
 *
 * 为什么不用通用 `PageHeader`：这页的身份信息量比别的页大（名称、状态、类型、
 * endpoint 都需要同屏可见），套用「图标 + 标题 + 副标题」会把端点挤到折行，
 * 且没有放操作按钮的位置。
 *
 * ⚠️ **不再展示运行时长**：Docker Engine API 不提供守护进程启动时刻，
 * 也不提供宿主 boot time（`/info` 的 `SystemTime` 是**当前时间**）。0.6.0 直接删除该字段。
 */
const props = defineProps<{
  /** 当前主机；未知 hostId 时为 null */
  host: HostOverview | null
  /** URL 上的主机名，主机查不到时用来兜底显示 */
  hostId: string
}>()

const emit = defineEmits<{ refresh: [] }>()

const { t } = useI18n()

const name = computed(() => props.host?.name ?? props.hostId)
const tone = computed(() => hostTone(props.host?.status))
const isOffline = computed(() => props.host?.status === 'offline')
const isMissing = computed(() => props.host == null)

const typeLabel = computed(() => {
  if (!props.host) return ''
  return props.host.type === 'socket' ? t('host.type.socket') : t('host.type.tcp')
})

const copied = ref(false)

/**
 * 复制 endpoint。
 *
 * clipboard API 在非安全上下文（http + 非 localhost）下不可用，
 * 因此失败时降级为提示而不是静默无响应——用户点了按钮必须有反馈。
 */
async function copyEndpoint() {
  if (!props.host) return
  try {
    await navigator.clipboard.writeText(props.host.endpoint)
    copied.value = true
    setTimeout(() => (copied.value = false), 1600)
  } catch {
    ElMessage.warning(t('common.copyFailed'))
  }
}
</script>

<template>
  <header class="app-card px-4 sm:px-5 py-4">
    <div class="flex items-start justify-between gap-4 flex-wrap">
      <div class="min-w-0">
        <!-- 主机名 + 状态药丸 + 类型药丸 -->
        <div class="flex items-center gap-2.5 flex-wrap">
          <StatusDot v-if="host" :status="host.status" size="lg" :pulse="false" />
          <h1 class="text-xl sm:text-2xl font-semibold tracking-tight text-content-strong app-truncate">
            {{ name }}
          </h1>

          <span v-if="!isMissing" class="app-chip" :class="HOST_TONE_CHIP[tone]">
            {{ t(HOST_TONE_LABEL_KEY[tone]) }}
          </span>

          <span class="app-chip bg-surface-sunken text-content-muted ring-line-subtle shrink-0">
            <span
              class="text-xs"
              :class="host?.type === 'socket' ? 'i-carbon-terminal' : 'i-carbon-cloud-satellite'"
            />
            {{ typeLabel }}
          </span>
        </div>

        <!-- endpoint：等宽 + 可复制。主机查不到时不渲染，避免展示空壳 -->
        <div v-if="host" class="mt-2 flex items-center gap-2 min-w-0">
          <code class="text-sm text-content-muted font-mono app-truncate" :title="host.endpoint">
            {{ host.endpoint }}
          </code>
          <button
            type="button"
            class="shrink-0 rounded p-1 text-content-faint transition-colors duration-200 hover:bg-surface-hover hover:text-content-body"
            :aria-label="t('host.hero.copyEndpoint')"
            :title="t('host.hero.copyEndpoint')"
            @click="copyEndpoint"
          >
            <span
              class="block text-sm"
              :class="copied ? 'i-carbon-checkmark text-status-ok' : 'i-carbon-copy'"
            />
          </button>
        </div>

        <p v-if="isMissing" class="mt-2 text-sm text-status-down">
          {{ t('host.hero.notFound') }}
        </p>
      </div>

      <!-- 操作区：刷新交由父组件处理（当前是本地占位，接口就绪后接真实请求） -->
      <div class="flex items-center gap-1.5 shrink-0">
        <el-tooltip :content="t('host.hero.refresh')" placement="bottom">
          <el-button
            text
            class="!px-2"
            :aria-label="t('host.hero.refresh')"
            @click="emit('refresh')"
          >
            <span class="i-carbon-renew text-lg" />
          </el-button>
        </el-tooltip>
      </div>
    </div>

    <!-- 离线提示：说明「为什么数字不动了」，否则用户会以为页面卡住 -->
    <p
      v-if="isOffline"
      class="mt-3 flex items-start gap-2 rounded-md bg-status-down-soft/50 px-3 py-2 text-xs text-status-down"
    >
      <span class="i-carbon-warning-alt shrink-0 mt-px text-sm" aria-hidden="true" />
      <span>{{ t('host.offline.desc') }}</span>
    </p>
  </header>
</template>
