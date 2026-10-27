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
 *
 * ## 三组元素必须分开（不是审美问题）
 * 工具条里其实有三件互不相干的事：
 *
 * | 组 | 回答的问题 | 元素 |
 * |----|-----------|------|
 * | 筛选 | 我要找哪几条 | 搜索框、状态下拉、命中计数 |
 * | 批量 | 要对这几条做什么 | 批量动作 + 已选计数 + 清空 |
 * | 页面 | 这一页本身怎么刷新 / 怎么显示 | 刷新、列设置 |
 *
 * 挤成一排时批量按钮会被读成搜索框的附属品——尤其批量是**图标按钮**的时候，
 * 视觉上更像筛选栏的一部分。所以组间给 `gap-x-4` 留白 + 一条 1px 竖线。
 *
 * ## 批量动作用「图标 + 文字」，不是纯图标
 * 行内动作图标化是对的（一行四五个，配文字会把表格撑宽），但工具条上的批量动作
 * 恰恰相反：它是「这一页支持批量操作」的唯一提示，也是最高频的那几个动作。
 * 纯图标时用户得逐个悬停去确认哪个是启动、哪个是删除——高频动作不该靠猜。
 * `IconActionButton` 的 `show-label` 就是这个模式（文字写在按钮上，tooltip 只留禁用原因）。
 *
 * ## 批量动作之间用 `gap-1`，与「已选 N / 取消选择」分开
 * 一排同类按钮应当读成一个**按钮组**（紧凑），而它们与「已选 N / 取消选择」
 * 是两类东西（后者是状态与撤销，不该挤进按钮组里）。
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
  <div class="flex flex-wrap items-center justify-end gap-x-4 gap-y-2">
    <!-- 第 1 组：筛选——搜索框、状态下拉、命中计数 -->
    <div class="flex flex-wrap items-center gap-2">
      <slot name="filters" />
    </div>

    <!--
      组间分隔线。

      ⚠️ **只在 `md` 以上显示**：窄屏下工具条会折行，竖线若正好落在新行的行首，
      看着像多刷了一道「|」，反而不如只靠 `gap-x-4` 的留白。

      ⚠️ 这里是**结构性**分隔，不是装饰：三组元素回答的是三个不同问题——
      「我要找哪几条」「要对它们做什么」「这一页本身怎么刷新 / 怎么显示」。
      挤在一起时批量按钮会被读成搜索框的附属品。
    -->
    <span
      v-if="$slots.batch"
      class="hidden h-4 w-px shrink-0 bg-line-subtle md:block"
      aria-hidden="true"
    />

    <!--
      第 2 组：批量动作 + 已选计数。
      「已选 N / 清空」紧跟批量按钮而不是放到最右：它们与批量动作是**同一件事的两半**
      （没有计数不知道勾到哪了，没有清空只能一行行取消），分开放会割裂这条因果。
    -->
    <div v-if="$slots.batch" class="flex flex-wrap items-center gap-2">
      <!--
        批量动作：**常驻**。
        槽位参数给出 count 与 busy，调用方据此禁用——
        「没勾选时点不动」是这里的关键，禁用原因由调用方写进 tooltip。

        按钮之间的间距压到 `gap-1`（4px）：批量动作是**一排同类按钮**，
        它们该读成一个整体（像按钮组），而不是一串互不相干的按钮。
        按钮与「已选 N / 取消选择」之间仍是 `gap-2`——那是另一类东西。
      -->
      <div class="flex flex-wrap items-center gap-1">
        <slot name="batch" :count="selectedCount" :busy="busy" />
      </div>

      <template v-if="selectedCount > 0">
        <span class="text-xs font-medium text-content-strong">
          {{ t('common.selected', { count: selectedCount }) }}
        </span>

        <el-button size="small" text :disabled="busy" @click="emit('clear')">
          {{ t('common.clearSelection') }}
        </el-button>
      </template>
    </div>

    <span
      v-if="$slots.extra"
      class="hidden h-4 w-px shrink-0 bg-line-subtle md:block"
      aria-hidden="true"
    />

    <!-- 第 3 组：页面级附加操作——刷新、列设置 -->
    <div class="flex items-center gap-1">
      <slot name="extra" />
    </div>
  </div>
</template>
