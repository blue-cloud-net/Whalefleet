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

/**
 * 分组描述同样走 i18n。
 *
 * 之前 `description` 存的是硬编码中文（'线上生产负载'），切到英文界面时
 * 分组标题变英文、描述却还是中文——与 `nameKey` 的处理方式不一致。
 */
const description = computed(() =>
  props.group?.descriptionKey ? t(props.group.descriptionKey) : undefined
)
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