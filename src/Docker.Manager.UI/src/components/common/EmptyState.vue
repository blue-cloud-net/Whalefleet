<script setup lang="ts">
import { useI18n } from 'vue-i18n'

withDefaults(
  defineProps<{
    icon?: string
    description?: string
    /** 紧凑模式：用于卡片内的小空态 */
    compact?: boolean
  }>(),
  { compact: false }
)

const { t } = useI18n()
</script>

<template>
  <div
    class="flex flex-col items-center justify-center text-center"
    :class="compact ? 'py-8' : 'py-16'"
  >
    <!-- 图标底盘：环形背景让空状态不那么「裸」 -->
    <div class="relative">
      <span
        class="absolute inset-0 rounded-full bg-brand-500/8 dark:bg-brand-500/10 blur-sm scale-110"
        aria-hidden="true"
      />
      <span
        class="relative text-5xl text-gray-300 dark:text-gray-600 block"
        :class="icon ?? 'i-carbon-document'"
        aria-hidden="true"
      />
    </div>

    <p
      class="mt-4 text-sm text-gray-500 dark:text-gray-400 max-w-sm leading-relaxed"
      :class="{ 'font-medium text-gray-600 dark:text-gray-300': !compact }"
    >
      {{ description ?? t('common.empty') }}
    </p>

    <div v-if="$slots.default" class="mt-5">
      <slot />
    </div>
  </div>
</template>