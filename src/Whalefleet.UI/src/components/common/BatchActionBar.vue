<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * 表格批量操作条：已选 N 项 + 一组动作。
 *
 * ## 为什么「选中后才出现」而不是常驻在工具栏里
 * 批量动作在**没有选中任何行时是不可用的**——常驻一组灰按钮会让人先怀疑页面坏了。
 * 反过来，选中后凭空冒出一条会打断视线，所以出入场用 EP 自带的 `el-collapse-transition`：
 * 它过渡的是**高度**，正好让上方表格平滑下移而不是突然被顶上去——
 * 不自己写 CSS 动画，是因为"展开时先撑高再淡入"的时序手写总差那么一点。
 *
 * ## 为什么动作由调用方给，而不是这里内置几个图标
 * 容器页的批量动作是启停 / 重启 / 删除，镜像页只有删除，文案与危险等级都不一样；
 * 内置就得在这写一堆 if。而"选中计数 + 动作插槽 + 清空"这三件事是完全通用的，
 * 抽出来正好是这里真正共用的部分。
 */
const props = withDefaults(
  defineProps<{
    /** 已选中的行数 */
    count: number
    /** 批量操作执行中：禁用全部动作 */
    busy?: boolean
  }>(),
  { busy: false }
)

const emit = defineEmits<{ (e: 'clear'): void }>()

const { t } = useI18n()

/** 没有选中时不渲染任何东西——见文件头第 1 条 */
const visible = computed(() => props.count > 0)
</script>

<template>
  <el-collapse-transition>
    <div
      v-if="visible"
      class="flex flex-wrap items-center gap-2 border-b border-line-subtle bg-surface-sunken px-4 py-2 sm:px-5"
    >
      <span class="text-xs font-medium text-content-strong">
        {{ t('common.selected', { count }) }}
      </span>

      <div class="flex flex-wrap items-center gap-2">
        <slot :busy="busy" />
      </div>

      <span class="flex-1" />

      <el-button size="small" text :disabled="busy" @click="emit('clear')">
        {{ t('common.clearSelection') }}
      </el-button>
    </div>
  </el-collapse-transition>
</template>
