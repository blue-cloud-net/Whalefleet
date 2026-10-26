<script setup lang="ts">
import { useI18n } from 'vue-i18n'

/**
 * 列表页工具条：搜索 / 筛选 + 常驻批量动作 + 右侧附加操作。
 *
 * ## 为什么批量动作**常驻**而不是「选中后才出现」
 * 早先的做法是选中一行才浮出一条批量操作条（旧版 `BatchActionBar`）。
 * 那种模型的毛病是**入口不可发现**：用户得先知道「勾选行之后会出现一条能批量启动的东西」，
 * 而页面上没有任何东西提示这件事。
 *
 * 常驻 + 未选中时禁用是更好的折中：按钮一直在那儿（用户知道这个页面支持批量操作），
 * 但没勾选时点不动（不会让人怀疑页面坏了）。
 *
 * ## 为什么动作由调用方给
 * 容器页是启停 / 重启 / 删除，镜像页只有删除，主机页只有批量打标——
 * 文案、图标、危险等级都不一样。内置就得在这写一堆 if，
 * 而「已选计数 + 动作插槽 + 清空」这三件事是完全通用的。
 *
 * ## 为什么「已选 N」与「清空」内置在这里
 * 它们与批量动作是**同一件事的两半**：没有计数，用户不知道刚才勾选到哪了；
 * 没有清空，勾错了只能一行行取消。这两件事各页面都有一份，行为必须一致。
 */
withDefaults(
  defineProps<{
    /** 已选中的行数；大于 0 时才显示「已选 N」与「清空」 */
    selectedCount?: number
    /** 批量操作执行中：禁用清空与全部批量动作 */
    busy?: boolean
  }>(),
  { selectedCount: 0, busy: false }
)

const emit = defineEmits<{ (e: 'clear'): void }>()

const { t } = useI18n()
</script>

<template>
  <div class="flex flex-wrap items-center justify-end gap-2">
    <!-- 筛选区：搜索框、下拉等 -->
    <slot name="filters" />

    <!--
      批量动作：**常驻**。
      槽位参数给出 count 与 busy，调用方据此禁用——
      「没勾选时点不动」是这里的关键，禁用原因由调用方写进 tooltip。
    -->
    <slot name="batch" :count="selectedCount" :busy="busy" />

    <template v-if="selectedCount > 0">
      <span class="text-xs font-medium text-content-strong">
        {{ t('common.selected', { count: selectedCount }) }}
      </span>

      <el-button size="small" text :disabled="busy" @click="emit('clear')">
        {{ t('common.clearSelection') }}
      </el-button>
    </template>

    <!-- 右侧附加操作：刷新、列设置等 -->
    <slot name="extra" />
  </div>
</template>
