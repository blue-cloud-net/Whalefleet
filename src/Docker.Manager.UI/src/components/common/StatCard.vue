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

/**
 * 色调对应的图标底色。
 *
 * 语义色的暗色变体由令牌自身翻转（`--status-ok` 暗色下是更亮的绿），
 * 因此这里不再写 `dark:` 变体——早先的写法会让暗色和亮色各维护一套色值。
 */
const TONE_ICON: Record<string, string> = {
  brand: 'text-brand-500 bg-brand-500/10',
  neutral: 'text-content-faint bg-surface-sunken',
  success: 'text-status-ok bg-status-ok-soft/60',
  warning: 'text-amber-500 bg-amber-500/10',
  danger: 'text-status-down bg-status-down-soft/60'
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
      <div class="text-xs font-medium text-content-muted app-truncate">
        {{ label }}
      </div>
      <div class="mt-1 flex items-baseline gap-1">
        <span class="text-2xl font-semibold leading-none app-tabular text-content-strong">
          {{ value ?? '—' }}
        </span>
        <span v-if="suffix && value != null" class="text-xs text-content-faint">
          {{ suffix }}
        </span>
      </div>
      <div v-if="hint" class="mt-1 text-xs text-content-faint app-truncate">
        {{ hint }}
      </div>
    </div>
  </div>
</template>