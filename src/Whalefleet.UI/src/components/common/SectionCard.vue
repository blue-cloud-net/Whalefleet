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
      class="flex items-center justify-between gap-3 px-4 sm:px-5 py-3 border-b border-line-subtle"
    >
      <div class="min-w-0">
        <h2 v-if="title" class="text-sm font-semibold text-content-strong app-truncate">
          {{ title }}
        </h2>
        <p v-if="description" class="mt-0.5 text-xs text-content-muted">
          {{ description }}
        </p>
      </div>
      <!--
        操作区**允许换行**：列表页的工具条（搜索 + 筛选 + 批量动作 + 刷新）
        在窄屏上放不下一行，硬撑会溢出卡片边界；换行只是让卡片头高一点，
        而溢出是「按钮跑到看不见的地方」。

        `min-w-0` 让它能被压缩，`justify-end` 让换行后的第二行仍然靠右对齐。
      -->
      <div v-if="$slots.actions" class="flex flex-wrap items-center justify-end gap-2 min-w-0">
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