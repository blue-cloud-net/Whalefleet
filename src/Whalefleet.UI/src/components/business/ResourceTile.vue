<script setup lang="ts">
import { computed } from 'vue'

/**
 * 资源计数磁贴：图标 + 名称 + 数值 + 副信息，整块可点进入对应资源页。
 *
 * **缺失态是这个组件的重点**。后端聚合接口就绪前 `counts` 可能整体缺失，
 * 此时最省事的做法是显示一个裸「—」，但那看起来像"数量为零"或"出问题了"。
 * 这里改为：整块降为弱化态 + tooltip 说明原因 + 数值位保持可读的占位符，
 * 让"没数据"和"真的是零"在视觉上可区分。
 */
const props = withDefaults(
  defineProps<{
    icon: string
    label: string
    /** null / undefined → 走缺失态 */
    value?: number | null
    /** 数值下方的补充说明，如「运行中 3 · 已停止 1」 */
    hint?: string
    /** 缺失原因：写进 title 与 aria-label，让屏幕阅读器也能读到 */
    missingHint?: string
  }>(),
  { value: null, hint: undefined, missingHint: undefined }
)

/**
 * 缺少数值：区分「真的是 0」与「没数据」的唯一依据。
 *
 * 弱化只作用在**数值位与图标**（各自的颜色已换），不用整块 opacity：
 * 整块降透明会把「容器 / 镜像」这类标签也压暗，反而更难读。
 */
const isMissing = computed(() => props.value == null)

const ariaLabel = computed(() =>
  isMissing.value
    ? `${props.label}：${props.missingHint ?? ''}`
    : props.hint
      ? `${props.label}：${props.value}（${props.hint}）`
      : `${props.label}：${props.value}`
)
</script>

<template>
  <button
    type="button"
    class="app-card app-card-interactive group flex items-center gap-3.5 px-4 py-3.5 text-left"
    :title="isMissing ? missingHint : undefined"
    :aria-label="ariaLabel"
  >
    <span
      class="w-10 h-10 shrink-0 rounded-lg grid place-items-center text-lg transition-colors duration-200"
      :class="
        isMissing
          ? 'bg-surface-sunken text-content-faint'
          : 'bg-brand-500/10 text-brand-600 dark:text-brand-400'
      "
      aria-hidden="true"
    >
      <span :class="icon" />
    </span>

    <span class="min-w-0 flex-1">
      <span class="block text-xs font-medium text-content-muted app-truncate">{{ label }}</span>

      <span
        class="mt-1.5 block text-2xl font-semibold leading-none app-tabular"
        :class="isMissing ? 'text-content-faint' : 'text-content-strong'"
      >
        {{ isMissing ? '—' : value }}
      </span>

      <span
        v-if="!isMissing && hint"
        class="mt-1.5 block text-xs text-content-faint app-truncate"
      >
        {{ hint }}
      </span>
    </span>

    <!-- 箭头常驻但极淡，hover 时才显形：既提示可点又不抢数值 -->
    <span
      class="i-carbon-chevron-right shrink-0 text-sm text-content-faint opacity-0 translate-x-[-2px] transition-all duration-200 group-hover:opacity-100 group-hover:translate-x-0 group-focus-visible:opacity-100"
      aria-hidden="true"
    />
  </button>
</template>
