<script setup lang="ts">
/**
 * 页面标题区：图标 + 标题 + 描述 + 右侧操作区。
 *
 * 十个页面都在重复「图标 + h1 + 副标题」的三件套，抽出来后：
 *  - 间距节奏统一（标题 20px、副标题 mt-1.5）
 *  - 移动端副标题可折叠，避免顶栏下方堆叠过高
 */
withDefaults(
  defineProps<{
    icon?: string
    title: string
    subtitle?: string
    /** 副标题最大宽度，防止长文撑满整行 */
    narrow?: boolean
  }>(),
  { narrow: false }
)
</script>

<template>
  <header class="mb-5 sm:mb-6">
    <div class="flex items-start justify-between gap-4 flex-wrap">
      <div class="min-w-0">
        <h1 class="app-page-title">
          <span
            v-if="icon"
            class="text-2xl text-brand-500 shrink-0 leading-none"
            :class="icon"
            aria-hidden="true"
          />
          <span class="app-truncate">{{ title }}</span>
          <slot name="badge" />
        </h1>

        <p v-if="subtitle" class="app-page-subtitle" :class="{ 'max-w-3xl': narrow }">
          {{ subtitle }}
        </p>
      </div>

      <!-- 右侧操作区：筛选器 / 主按钮等 -->
      <div v-if="$slots.actions" class="flex items-center gap-2 shrink-0">
        <slot name="actions" />
      </div>
    </div>

    <!-- 标题区下方的次级内容（如统计条、标签页） -->
    <slot name="extra" />
  </header>
</template>