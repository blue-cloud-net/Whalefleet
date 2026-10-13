<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { HostGroup } from '@/types/api'

const props = defineProps<{
  /** null = 「未分组」分组 */
  group: HostGroup | null
  count: number
}>()

const { t } = useI18n()

const title = computed(() => t(props.group?.nameKey ?? 'host.group.ungrouped'))
const icon = computed(() => (props.group === null ? 'i-carbon-folder-off' : 'i-carbon-folder'))
</script>

<template>
  <section>
    <header class="flex items-center gap-2.5 mb-2.5">
      <!-- 品牌色竖条：作为分组视觉锚点，扫视时快速定位 -->
      <span class="w-0.5 h-4 rounded-full bg-brand-500/70 shrink-0" aria-hidden="true" />
      <span class="text-base text-gray-400 dark:text-gray-500 shrink-0" :class="icon" />
      <h2 class="text-sm font-semibold text-gray-700 dark:text-gray-200 app-truncate">
        {{ title }}
      </h2>
      <span
        class="app-chip shrink-0 bg-gray-100 dark:bg-gray-800 text-gray-500 dark:text-gray-400 ring-gray-200/70 dark:ring-gray-700/50 app-tabular"
      >
        {{ count }}
      </span>
    </header>

    <p v-if="group?.description" class="mb-2 ml-3 text-xs text-gray-400 dark:text-gray-500">
      {{ group.description }}
    </p>

    <slot />
  </section>
</template>