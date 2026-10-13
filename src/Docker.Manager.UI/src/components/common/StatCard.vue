<script setup lang="ts">
/**
 * 统计磁贴：图标 + 数值 + 标签。
 *
 * 数值缺失时统一显示「—」而非 0，避免把「没数据」误读成「真的是零」。
 */
withDefaults(
  defineProps<{
    icon?: string
    /** null / undefined → 显示占位符 */
    value?: number | string | null
    label: string
    /** 强调色：brand 跟随主题，其余用于区分状态语义 */
    tone?: 'brand' | 'neutral' | 'success' | 'warning' | 'danger'
    /** 数值后缀，如「台」「个」 */
    suffix?: string
    hint?: string
  }>(),
  { tone: 'neutral' }
)

const TONE_ICON: Record<string, string> = {
  brand: 'text-brand-500 bg-brand-50 dark:bg-brand-500/15',
  neutral: 'text-gray-400 dark:text-gray-500 bg-gray-100 dark:bg-gray-700/60',
  success: 'text-green-600 dark:text-green-500 bg-green-50 dark:bg-green-500/15',
  warning: 'text-amber-600 dark:text-amber-500 bg-amber-50 dark:bg-amber-500/15',
  danger: 'text-red-600 dark:text-red-500 bg-red-50 dark:bg-red-500/15'
}
</script>

<template>
  <div class="app-card-interactive p-4 flex items-start gap-3.5">
    <span
      v-if="icon"
      class="w-10 h-10 rounded-lg grid place-items-center text-lg shrink-0"
      :class="TONE_ICON[tone]"
      aria-hidden="true"
    >
      <span :class="icon" />
    </span>

    <div class="min-w-0 flex-1">
      <div class="text-xs font-medium text-gray-500 dark:text-gray-400 app-truncate">
        {{ label }}
      </div>
      <div class="mt-1 flex items-baseline gap-1">
        <span class="text-2xl font-semibold leading-none app-tabular text-gray-900 dark:text-gray-100">
          {{ value ?? '—' }}
        </span>
        <span v-if="suffix && value != null" class="text-xs text-gray-400 dark:text-gray-500">
          {{ suffix }}
        </span>
      </div>
      <div v-if="hint" class="mt-1 text-xs text-gray-400 dark:text-gray-500 app-truncate">
        {{ hint }}
      </div>
    </div>
  </div>
</template>