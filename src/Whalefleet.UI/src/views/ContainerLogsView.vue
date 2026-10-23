<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import LogViewport from '@/components/business/LogViewport.vue'
import { useContainerLogStream } from '@/composables/useContainerLogStream'
import { downloadContainerLogs } from '@/api/container'

/**
 * 容器日志页（0.5.0 重写）。
 *
 * ## 0.5.0 之前
 * 一次性读取整段文本，页面不会自己长出新行——「实时跟随」在静态文本上没有意义。
 *
 * ## 0.5.0 之后
 * 首屏历史 + SignalR 实时流；日志是**结构化行**（有序号、有时间戳），
 * 视图用虚拟滚动只渲染视口附近的行，向上滚可以加载更早的日志。
 *
 * ## 三个开关的分工
 * - **实时跟随**（页面级，默认开）：新行到达时视图滚到底部。
 *   用户主动往上滚会自动关掉它——不关的话「翻旧日志」会被一次次拽回底部。
 * - **暂停**：停止把新行并入视图（到达的行会缓冲，恢复后补上）。
 * - **换行**：长行折行显示（行高可变，滚动位置计算更贵）。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const hostId = computed(() => String(route.params.hostId ?? ''))
const containerId = computed(() => String(route.params.id ?? ''))

/** 首屏拉取的尾行数。 */
const tail = ref(500)

/** 是否跟随最新日志。 */
const follow = ref(true)

/** 是否换行。 */
const wrap = ref(true)

/** 是否显示时间戳列。 */
const showTimestamp = ref(true)

/** 是否含 stderr。 */
const includeStderr = ref(true)

/** 下载中。 */
const downloading = ref(false)

const stream = useContainerLogStream({
  hostId: () => hostId.value,
  containerId: () => containerId.value,
  tail: () => tail.value
})

const lines = stream.lines
const status = stream.status
const error = stream.error

/** 是否有日志可显示。 */
const hasLines = computed(() => lines.value.length > 0)

/** 状态文案。 */
const statusText = computed(() => {
  switch (status.value) {
    case 'loading':
      return t('container.logs.status.loading')
    case 'live':
      return t('container.logs.status.live')
    case 'error':
      return t('container.logs.status.error')
    default:
      return t('container.logs.status.idle')
  }
})

/** 状态点的颜色类。 */
const statusClass = computed(() => {
  switch (status.value) {
    case 'live':
      return 'bg-success'
    case 'error':
      return 'bg-danger'
    case 'loading':
      return 'bg-warning'
    default:
      return 'bg-content-muted'
  }
})

async function load(): Promise<void> {
  await stream.load()
}

/** 下载完整日志。 */
async function download(): Promise<void> {
  if (downloading.value) return

  downloading.value = true
  try {
    const { blob, fileName } = await downloadContainerLogs(hostId.value, containerId.value, {
      includeStderr: includeStderr.value
    })

    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = fileName
    document.body.appendChild(anchor)
    anchor.click()
    document.body.removeChild(anchor)
    // 立刻 revoke 会让部分浏览器来不及开始下载。
    setTimeout(() => URL.revokeObjectURL(url), 10_000)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    downloading.value = false
  }
}

function backToList(): void {
  void router.push({ name: 'host-containers', params: { hostId: hostId.value } })
}

watch(containerId, () => {
  void load()
})

onMounted(load)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-cube"
      :title="t('container.logs.title')"
      :subtitle="t('container.logs.subtitle')"
      narrow
    >
      <template #actions>
        <el-button @click="backToList">{{ t('container.backToList') }}</el-button>
        <el-button :loading="downloading" @click="download">
          {{ t('container.logs.download') }}
        </el-button>
        <el-button :loading="status === 'loading'" type="primary" @click="load">
          {{ t('container.action.refresh') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <div class="flex flex-wrap items-center gap-4">
          <!-- 状态点：一眼看出实时通道是否连着。 -->
          <span class="flex items-center gap-1.5 text-xs text-content-muted">
            <span class="inline-block h-2 w-2 rounded-full" :class="statusClass" />
            {{ statusText }}
          </span>

          <label class="flex items-center gap-1.5 text-xs whitespace-nowrap text-content-muted">
            <span>{{ t('container.logs.tail') }}</span>
            <el-select v-model="tail" size="small" class="w-28" @change="load">
              <el-option
                v-for="option in [200, 500, 1000, 5000]"
                :key="option"
                :value="option"
                :label="String(option)"
              />
            </el-select>
          </label>

          <el-checkbox v-model="follow" size="small" class="whitespace-nowrap">
            {{ t('container.logs.follow') }}
          </el-checkbox>

          <el-checkbox v-model="showTimestamp" size="small" class="whitespace-nowrap">
            {{ t('container.logs.timestamps') }}
          </el-checkbox>

          <el-checkbox v-model="wrap" size="small" class="whitespace-nowrap">
            {{ t('container.logs.wrap') }}
          </el-checkbox>

          <el-checkbox v-model="includeStderr" size="small" class="whitespace-nowrap">
            {{ t('container.logs.includeStderr') }}
          </el-checkbox>

          <el-button
            size="small"
            :type="stream.paused.value ? 'primary' : 'default'"
            @click="stream.paused.value ? stream.resume() : stream.pause()"
          >
            {{ stream.paused.value ? t('container.logs.resume') : t('container.logs.pause') }}
          </el-button>
        </div>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <LogViewport
        v-else-if="hasLines"
        :lines="lines"
        :mutation="stream.mutation.value"
        :follow="follow"
        :paused="stream.paused.value"
        :has-more="stream.hasMore.value"
        :loading-older="stream.loadingOlder.value"
        :wrap="wrap"
        :show-timestamp="showTimestamp"
        :dropped="stream.dropped.value"
        @update:follow="follow = $event"
        @load-older="stream.loadOlder()"
      />

      <EmptyState
        v-else
        class="p-6"
        icon="i-carbon-document"
        :title="t('container.logs.empty')"
        :description="t('container.logs.emptyHint')"
      />
    </SectionCard>
  </div>
</template>
