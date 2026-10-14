<script setup lang="ts">
/**
 * 「功能预告」占位区。
 *
 * 用来替换 0.0.7 时那种「大卡片 + 一句话」的占位页。那种写法的问题在于
 * 它长得**太像故障**：一块空白卡片加一句「等待后端接口」，用户分不清是
 * 页面坏了、功能没做、还是这台主机正好没有数据。
 *
 * 这里把它明确成第三种语义——**尚未实现，但已在排期里**：
 *  - 细环图标 + 版本徽章，一眼区别于实心的空态（真的没数据）与骨架屏（正在加载）
 *  - 列出该页将提供的关键能力，让占位页仍有信息量
 *  - 底部注明排期依据，代码评审时能直接对照 roadmap
 */
withDefaults(
  defineProps<{
    icon: string
    /** 一句话说清这页将来负责什么 */
    title: string
    /** 计划交付版本，如 `0.0.11`；不传则不显示版本徽章 */
    version?: string
    /** 该页将提供的关键能力，逐条列出 */
    capabilities?: string[]
    /** 补充说明（依赖、限制等） */
    note?: string
  }>(),
  { version: undefined, capabilities: () => [], note: undefined }
)
</script>

<template>
  <div class="app-card">
    <div class="px-6 py-10 sm:py-12 text-center">
      <!-- 图标底盘：细环而非大面积光晕，避免在浅色背景上糊成一块 -->
      <span
        class="inline-grid place-items-center w-14 h-14 rounded-full ring-1 ring-line-subtle text-2xl text-content-faint"
        aria-hidden="true"
      >
        <span :class="icon" />
      </span>

      <div class="mt-4 flex items-center justify-center gap-2 flex-wrap">
        <h2 class="text-sm font-semibold text-content-strong">{{ title }}</h2>
        <span
          v-if="version"
          class="app-chip bg-brand-500/10 text-brand-600 dark:text-brand-400 ring-brand-500/25 app-tabular"
        >
          {{ version }}
        </span>
      </div>

      <!-- 能力清单：让占位页也有实质内容，而不是一句「暂无」 -->
      <ul
        v-if="capabilities.length > 0"
        class="mx-auto mt-5 flex max-w-xl flex-wrap items-center justify-center gap-2"
      >
        <li
          v-for="cap in capabilities"
          :key="cap"
          class="app-chip bg-surface-sunken text-content-muted ring-line-subtle"
        >
          {{ cap }}
        </li>
      </ul>

      <p v-if="note" class="mx-auto mt-5 max-w-lg text-xs leading-relaxed text-content-faint">
        {{ note }}
      </p>
    </div>
  </div>
</template>