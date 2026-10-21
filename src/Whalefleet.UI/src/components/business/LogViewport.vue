<script setup lang="ts">
import { computed, nextTick, onMounted, onUpdated, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useVirtualRows } from '@/composables/useVirtualRows'
import type { LogMutation } from '@/composables/useContainerLogStream'
import { formatLogTime } from '@/utils/log-format'
import type { ContainerLogLine } from '@/types/api'

/**
 * 容器日志的虚拟滚动视口（0.5.0）。
 *
 * ## 为什么只渲染一小段
 * 日志动辄几万行。全量渲染会让 DOM 节点数与日志行数同阶，
 * 滚动卡顿、内存暴涨，而且**新行到达时整棵树都要重排**。
 * 这里只渲染视口附近约 500 行，其余用一块等高的「占位」撑出滚动条。
 *
 * ## 两种行高模式
 * - **不换行**（默认）：每行固定高度，位置是纯算术，最省。
 * - **换行**：行高可变，用估算值占位、实测后修正。
 *
 * ## 向上加载更早的日志
 * 滚到顶部时请求更早的一页，插入到列表**头部**。
 * 插入会让视口上方的内容变高 → 视觉上会把用户「顶走」。
 * 因此插入后要把滚动位置**同步下移同样的高度**（锚点保持）——
 * 这就是 `applyAnchor` 的作用。
 */
const { t } = useI18n()

/** 固定行高（px）。与 `log-row` 的字号 / 行距对应。 */
const ROW_HEIGHT = 20

/** 可变行高模式下的估算行高（px）。 */
const ESTIMATED_ROW_HEIGHT = 20

/** 距离顶部多少像素时触发「加载更早的日志」。 */
const LOAD_OLDER_THRESHOLD = 32

/** 判定「已在底部」的容差（px）。 */
const BOTTOM_TOLERANCE = 4

const props = defineProps<{
  /** 全部日志行。 */
  lines: ContainerLogLine[]
  /** 行集合变更描述（驱动虚拟列表的追加 / 前插）。 */
  mutation: LogMutation
  /** 是否跟随最新日志。 */
  follow: boolean
  /** 是否暂停接收。 */
  paused: boolean
  /** 是否可能还有更早的日志。 */
  hasMore: boolean
  /** 是否正在加载更早的日志。 */
  loadingOlder: boolean
  /** 是否换行显示。 */
  wrap: boolean
  /** 是否显示时间戳列。 */
  showTimestamp: boolean
  /** 因拥塞丢弃的行数。 */
  dropped: number
}>()

const emit = defineEmits<{
  (event: 'update:follow', value: boolean): void
  (event: 'load-older'): void
}>()

const viewport = ref<HTMLElement | null>(null)
const viewportHeight = ref(0)

const rows = useVirtualRows({
  viewportHeight,
  rowHeight: ROW_HEIGHT,
  estimatedRowHeight: () => (props.wrap ? ESTIMATED_ROW_HEIGHT : null),
  overscan: 12
})

/** 上一次滚动位置（用来判断用户是不是在往上滚）。 */
let lastTop = 0
/** 上一次内容总高度（锚点修正的基准）。 */
let lastScrollHeight = 0
/** 是否需要做锚点修正。 */
let anchorPending = false
/** 是否已经请求过「加载更早」。 */
let olderRequested = false
/** 上一次测量时用的键（避免重复测量）。 */
let lastMeasureKey = ''
/** 连续测量的轮数（防止极端情况下的来回抖动）。 */
let measureRounds = 0

/** 当前渲染窗口内的行。 */
const visibleRows = computed(() => {
  const { startIndex, endIndex } = rows.view.value
  return props.lines.slice(startIndex, endIndex).map((line, offset) => ({
    line,
    index: startIndex + offset
  }))
})

/**
 * 实测可见行的高度。
 *
 * 只在「测量键」变化时测——键由「变更版本 + 窗口范围」组成，
 * 这样每次变更最多测两轮（第一轮测出真实高度触发重排，第二轮确认无变化）。
 */
function measureRows(): void {
  const element = viewport.value
  if (!element) return

  const view = rows.view.value
  for (const row of Array.from(element.querySelectorAll<HTMLElement>('[data-log-index]'))) {
    const index = Number(row.dataset.logIndex)
    if (!Number.isInteger(index)) continue
    if (index < view.startIndex || index >= view.endIndex) continue

    const height = row.offsetHeight
    if (height > 0) {
      rows.measure(index, height)
    }
  }
}

/** 把滚动位置修正回去（插入更早的日志后保持视觉锚点）。 */
function applyAnchor(): void {
  const element = viewport.value
  if (!element) return

  const delta = element.scrollHeight - lastScrollHeight
  if (delta > 0) {
    element.scrollTop += delta
  }
}

/** 滚到底部。 */
function scrollToBottom(): void {
  const element = viewport.value
  if (!element) return
  element.scrollTop = element.scrollHeight
}

/** 滚动处理。 */
function onScroll(): void {
  const element = viewport.value
  if (!element) return

  const top = element.scrollTop
  const atBottom = top + element.clientHeight >= element.scrollHeight - BOTTOM_TOLERANCE

  // 用户主动往上滚 → 停止跟随。
  // 不这么做的话，「往上翻看旧日志」会被新行一次次拽回底部。
  if (props.follow && !atBottom && top < lastTop) {
    emit('update:follow', false)
  }

  lastTop = top
  rows.onScroll(top)

  if (top < LOAD_OLDER_THRESHOLD && props.hasMore && !props.loadingOlder && !olderRequested) {
    olderRequested = true
    emit('load-older')
  }
}

/** 行集合变更 → 虚拟列表同步。 */
watch(
  () => props.mutation.version,
  () => {
    const mutation = props.mutation

    if (mutation.kind === 'reset') {
      rows.reset(props.lines.length)
      lastScrollHeight = viewport.value?.scrollHeight ?? 0
      if (props.follow) {
        void nextTick(() => scrollToBottom())
      }
      return
    }

    if (mutation.kind === 'prepend') {
      // 先记下插入前的高度，渲染后据此把滚动位置补回去。
      lastScrollHeight = viewport.value?.scrollHeight ?? 0
      rows.prepend(mutation.count)
      anchorPending = true
      return
    }

    rows.append(mutation.count)
    if (props.follow) {
      void nextTick(() => scrollToBottom())
    }
  },
  { immediate: true }
)

// 「换行」开关一换，所有行的高度都变了 → 已实测的高度全部作废。
watch(
  () => props.wrap,
  () => {
    rows.reset(props.lines.length)
    measureRounds = 0
    lastMeasureKey = ''
    void nextTick(() => scrollToBottom())
  }
)

// 上一次加载结束 → 允许再次触发「加载更早」。
watch(
  () => props.loadingOlder,
  (value) => {
    if (!value) olderRequested = false
  }
)

onMounted(() => {
  viewportHeight.value = viewport.value?.clientHeight ?? 0
})

onUpdated(() => {
  const view = rows.view.value
  const key = `${props.mutation.version}:${view.startIndex}:${view.endIndex}`

  if (key !== lastMeasureKey && measureRounds < 4) {
    lastMeasureKey = key
    measureRounds += 1
    measureRows()
  }

  if (anchorPending) {
    anchorPending = false
    applyAnchor()
  }

  lastScrollHeight = viewport.value?.scrollHeight ?? 0
})
</script>

<template>
  <div class="relative">
    <!-- 顶部提示：丢行 / 暂停缓冲 / 还有更多 -->
    <div
      v-if="dropped > 0 || props.paused || props.hasMore"
      class="absolute inset-x-0 top-0 z-10 flex flex-col gap-1 p-2"
    >
      <div
        v-if="dropped > 0"
        class="rounded bg-warning/15 px-2 py-1 text-xs text-warning"
      >
        {{ t('container.logs.dropped', { count: dropped }) }}
      </div>
      <div
        v-if="props.paused"
        class="rounded bg-info/15 px-2 py-1 text-xs text-info"
      >
        {{ t('container.logs.paused') }}
      </div>
    </div>

    <div
      ref="viewport"
      class="app-log-viewport"
      @scroll.passive="onScroll"
    >
      <!-- 占位块：撑出滚动条的高度 -->
      <div
        class="relative"
        :style="{ height: `${rows.view.value.totalHeight}px` }"
      >
        <!-- 实际渲染的行 -->
        <div
          class="absolute left-0 right-0 top-0"
          :style="{ transform: `translateY(${rows.view.value.offsetY}px)` }"
        >
          <div
            v-for="row in visibleRows"
            :key="row.index"
            :data-log-index="row.index"
            class="app-log-row"
            :class="{ 'app-log-row--wrap': props.wrap }"
          >
            <span class="app-log-seq">{{ row.line.seq }}</span>
            <span v-if="props.showTimestamp" class="app-log-time">
              {{ formatLogTime(row.line.timestamp) }}
            </span>
            <span class="app-log-text" :title="row.line.line">{{ row.line.line }}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- 不在底部时显示「回到底部」 -->
    <div
      v-if="!props.follow"
      class="pointer-events-none absolute inset-x-0 bottom-3 flex justify-center"
    >
      <button
        type="button"
        class="pointer-events-auto rounded-full bg-surface shadow-lg px-3 py-1 text-xs text-content hover:bg-surface-hover"
        @click="emit('update:follow', true)"
      >
        {{ t('container.logs.backToBottom') }}
      </button>
    </div>
  </div>
</template>

<style scoped>
/*
 * 日志行的样式放在 scoped 里而不是 UnoCSS 原子类：
 * 「行高必须精确等于 ROW_HEIGHT」是虚拟滚动的硬约束，
 * 而 UnoCSS 的行高类（leading-*）一旦被主题改写，位置计算就会整体偏移。
 */
.app-log-viewport {
  height: 60vh;
  overflow: auto;
  background: var(--wf-log-bg, transparent);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  line-height: 20px;
  contain: strict;
}

.app-log-row {
  display: flex;
  gap: 0.75rem;
  padding: 0 1rem;
  height: 20px;
  align-items: center;
  white-space: pre;
}

.app-log-row--wrap {
  height: auto;
  min-height: 20px;
  white-space: pre-wrap;
  word-break: break-all;
  align-items: flex-start;
}

.app-log-seq {
  flex: 0 0 auto;
  min-width: 3.5rem;
  text-align: right;
  color: var(--el-text-color-secondary);
  user-select: none;
}

.app-log-time {
  flex: 0 0 auto;
  color: var(--el-text-color-secondary);
  user-select: none;
}

.app-log-text {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.app-log-row--wrap .app-log-text {
  overflow: visible;
  text-overflow: clip;
}
</style>
