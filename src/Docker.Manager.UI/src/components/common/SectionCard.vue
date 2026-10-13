<script setup lang="ts">
/**
 * 带标题的内容区块：标题 + 右侧操作 + 内容。
 *
 * 与 `app-card` 的区别是它提供统一的标题排版与分隔，
 * 避免每个 section 都手写一遍 h2 + border-b。
 */
withDefaults(
  defineProps<{
    title?: string
    description?: string
    /** 内容区内边距，dense 用于表格类紧凑场景 */
    density?: 'normal' | 'dense' | 'flush'
  }>(),
  { density: 'normal' }
)
</script>

<template>
  <section class="app-card overflow-hidden">
    <header
      v-if="title || $slots.actions"
      class="flex items-center justify-between gap-3 px-4 sm:px-5 py-3 border-b border-gray-200 dark:border-gray-700"
    >
      <div class="min-w-0">
        <h2 v-if="title" class="text-sm font-semibold text-gray-900 dark:text-gray-100 app-truncate">
          {{ title }}
        </h2>
        <p v-if="description" class="mt-0.5 text-xs text-gray-500 dark:text-gray-400">
          {{ description }}
        </p>
      </div>
      <div v-if="$slots.actions" class="flex items-center gap-2 shrink-0">
        <slot name="actions" />
      </div>
    </header>

    <div
      :class="{
        'p-4 sm:p-5': density === 'normal',
        'p-3 sm:p-4': density === 'dense',
        '': density === 'flush'
      }"
    >
      <slot />
    </div>
  </section>
</template>