<script setup lang="ts">
import { useI18n } from 'vue-i18n'

/**
 * 空状态：这一块区域确实没有内容。
 *
 * 与另外两种「非正常态」的区别很重要，不要混用：
 *  - 空态（本组件）：接口通了、数据为空 → 该引导用户去创建 / 清除筛选
 *  - 预告（`PlaceholderSection`）：功能还没做 → 该说明排期
 *  - 加载中：**不用本组件**，按约定用 `el-skeleton`（骨架形状与真实布局一致，不出文案）
 *
 * `tone` 用于区分「空的正常原因」（neutral）与「出问题了」（error）：
 * 「没查到数据」和「请求失败」如果都画成灰色卡片，用户会以为是自己操作的问题。
 */
withDefaults(
  defineProps<{
    icon?: string
    description?: string
    /** 紧凑模式：用于卡片内的小空态 */
    compact?: boolean
    tone?: 'neutral' | 'warning' | 'error'
  }>(),
  { compact: false, tone: 'neutral' }
)

const TONE_RING: Record<string, string> = {
  neutral: 'ring-line-subtle text-content-faint',
  warning: 'ring-amber-500/25 text-amber-500',
  error: 'ring-status-down/25 text-status-down'
}

const TONE_TEXT: Record<string, string> = {
  neutral: 'text-content-muted',
  warning: 'text-amber-500',
  error: 'text-status-down'
}

const { t } = useI18n()
</script>

<template>
  <div
    class="flex flex-col items-center justify-center text-center"
    :class="compact ? 'py-8' : 'py-16'"
  >
    <!--
      图标底盘：一圈细环而非大面积模糊光晕。
      模糊光晕（blur-sm + brand-500）在浅色背景上会糊成一团脏色，
      而且它在暗色下几乎不可见，等于白付了一次渲染开销。
    -->
    <span
      class="inline-grid place-items-center w-14 h-14 rounded-full ring-1 text-3xl"
      :class="TONE_RING[tone]"
      aria-hidden="true"
    >
      <span :class="icon ?? 'i-carbon-document'" />
    </span>

    <p
      class="mt-4 text-sm max-w-sm leading-relaxed"
      :class="[TONE_TEXT[tone], { 'font-medium': !compact }]"
    >
      {{ description ?? t('common.empty') }}
    </p>

    <div v-if="$slots.default" class="mt-5">
      <slot />
    </div>
  </div>
</template>