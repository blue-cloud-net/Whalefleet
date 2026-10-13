<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { groupDescription, groupLabel } from '@/utils/group-display'
import type { HostGroup } from '@/types/api'

const props = defineProps<{
  /** null = 「未分组」分组 */
  group: HostGroup | null
  count: number
}>()

const { t } = useI18n()

/** 内置分组取 i18n 文案，自定义分组原样显示管理员输入的名字。 */
const title = computed(() => groupLabel(props.group, t))
const icon = computed(() => (props.group === null ? 'i-carbon-folder-off' : 'i-carbon-folder'))

/** 管理员写的描述优先；没写时内置分组回退到内置文案，自定义分组不显示描述。 */
const description = computed(() => groupDescription(props.group, t))
</script>

<template>
  <section>
    <header class="flex items-center gap-2.5 mb-2.5">
      <!-- 品牌色竖条：作为分组视觉锚点，扫视时快速定位 -->
      <span class="w-0.5 h-4 rounded-full bg-brand-500/70 shrink-0" aria-hidden="true" />
      <span class="text-base text-content-faint shrink-0" :class="icon" />
      <h2 class="text-sm font-semibold text-content-strong app-truncate">
        {{ title }}
      </h2>
      <span
        class="app-chip shrink-0 bg-surface-sunken text-content-muted ring-line-subtle app-tabular"
      >
        {{ count }}
      </span>
    </header>

    <p v-if="description" class="mb-2 ml-3 text-xs text-content-faint">
      {{ description }}
    </p>

    <slot />
  </section>
</template>