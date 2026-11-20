<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useIsNarrow } from '@/composables/useMediaQuery'
import {
  MAX_PAGE_SIZE,
  PAGE_SIZE_ALL,
  PAGE_SIZE_OPTIONS,
  resolvePageSize,
  usePreferencesStore
} from '@/stores/preferences'

/**
 * 列表分页条。
 *
 * ## 排列顺序：切页 → 调页 → 总数 → 每页条数
 * 顺序按**操作频率**排：翻页与跳页是高频动作，放在最左边手不用离开鼠标；
 * 总数是"我现在看到多少"的背景信息；每页条数是低频设置，放最右。
 * 整体居中——它是一条独立于表格的控件条，靠左会和表格内容边线错开半个视觉重心。
 *
 * ## 为什么页码部分用 `el-pagination`，每页条数却用旁边的 `el-select`
 * `el-pagination` 的 `sizes` 内置了页码按钮与总数，但它每个选项的 label 是
 * `item + t('el.pagination.pagesize')` **硬编码**的（见 EP 源码 sizes.vue），
 * 没有插槽可以改成「全部」。要出现「全部」两个字，条数选择就只能挪出来单独做，
 * 而 `el-select` 同样是 EP 组件——不是自己造控件。
 *
 * ## 0-based ↔ 1-based 的换算只在这一处
 * 路由、store、后端请求全用 0 起的 `pageIndex`，而 `el-pagination` 用 1 起。
 * 两套下标混着写必然 off-by-one，所以换算只发生在这个组件的 prop / event 边界上。
 *
 * ## pageSize 走 preferences store
 * 每页条数是跨页面偏好：设置页能改，列表页也能改，两处必须是同一个值。
 * 组件只把新值写回 store 并抛事件，不自己发请求——它不知道数据从哪来。
 *
 * ## 窄屏为什么换掉 `layout`
 *
 * 宽屏的 `prev, pager, next, jumper, total` 在 390px 宽的手机上**必然撑破容器**：
 * 翻页 2 个 + 页码最多 7 个 + 跳页输入框 + 总数文案，再加旁边的每页条数下拉，
 * 少说也要 500px。症状是分页条被裁掉右半截（「20 条 /」变成「20 条 /」后半截消失），
 * 而横向溢出在移动端还会让整个页面可横向拖动——表格也随之被拖出视野。
 *
 * 窄屏改成两行：
 *   第一行：`prev, pager, next`（居中）
 *   第二行：总数 + 每页条数
 * 页码按钮数也从 7 压到 5，否则 9 个按钮本身就占满一行。
 *
 * ⚠️ 这里换的是 **EP 组件渲染哪些子节点**，CSS 做不到（只能藏不能少），
 * 所以才引入 `useIsNarrow`；工具条那种纯排版问题仍然走 CSS。
 */
const props = withDefaults(
  defineProps<{
    /** 总条数（分页前） */
    total: number
    /** 当前页码，从 0 开始（与后端 / store 一致） */
    pageIndex: number
    /** 当前每页条数；`PAGE_SIZE_ALL`（0）表示「全部」 */
    pageSize: number
    /**
     * 每页条数上限，与后端 `MaxPageSize` 一致。
     * 超过它的档位（如 200）会被后端夹紧，用户选了却没生效且无任何提示。
     */
    maxPageSize?: number    /** 加载中：禁用整条分页，防止连点翻出重复页 */
    disabled?: boolean
  }>(),
  { maxPageSize: MAX_PAGE_SIZE, disabled: false }
)

const emit = defineEmits<{
  (e: 'update:pageIndex', value: number): void
  (e: 'update:pageSize', value: number): void
}>()

const { t } = useI18n()
const prefs = usePreferencesStore()

/**
 * 窄屏（<640px）判定。
 *
 * ⚠️ 这里必须用 JS 而不是 CSS：`layout` 决定 `el-pagination` **渲染哪些子节点**，
 * CSS 只能把它们藏起来（`display:none`）——藏起来的节点仍在 DOM 里，
 * 依然参与布局测量，横向溢出不会消失。
 */
const isNarrow = useIsNarrow()

/** 窄屏只留「上一页 / 页码 / 下一页」；跳页输入框与总数挪到第二行 */
const paginationLayout = computed(() =>
  isNarrow.value ? 'prev, pager, next' : 'prev, pager, next, jumper, total'
)

/**
 * 页码按钮数量。
 *
 * EP 默认 7 个（加上翻页共 9 个按钮），窄屏一行放不下；
 * 压到 5 个（连翻页共 7 个）刚好落在一行内。
 */
const pagerCount = computed(() => (isNarrow.value ? 5 : 7))

/** 真正交给 `el-pagination` 的每页条数：0 在它内部会让页数算成 Infinity。 */
const effectivePageSize = computed(() => resolvePageSize(props.pageSize, props.total))

/** 只列出不超过后端上限的档位 */
const sizeOptions = computed(() => PAGE_SIZE_OPTIONS.filter((size) => size <= props.maxPageSize))

/**
 * 「全部」的文案。
 *
 * 条目数超过后端上限时**必须如实标出上限**：此时「全部」实际只能拿到 `maxPageSize` 条，
 * 只写「全部」就是在骗人——用户会以为列表里是全部数据。
 * 上限内才显示干净的「全部」。
 */
const allOptionLabel = computed(() =>
  props.total > props.maxPageSize
    ? t('common.pageSizeAllCapped', { max: props.maxPageSize })
    : t('common.pageSizeAll')
)

/** `el-pagination` 的页码是 1 起，这里把 0 起换过去 */
const currentPage = computed(() => props.pageIndex + 1)

function onPageChange(next: number): void {
  // `next` 已是 1 起；与当前页相同时直接忽略——EP 受控模式下也会回抛同值，
  // 不挡掉的话每次重渲染都会多打一次请求。
  if (next - 1 === props.pageIndex) return

  emit('update:pageIndex', next - 1)
}

/**
 * 换每页条数。
 *
 * ⚠️ 必须回到第 0 页：原来在第 5 页（共 7 页）时把每页从 20 改成 50，
 * 总页数变成 1，此时 pageIndex=4 已越界——请求会拿到空列表，
 * 表格闪一下空白再自己跳回来，中间那一瞬像是"数据丢了"。
 *
 * ⚠️ 这里**同时**抛 `update:pageSize` 与 `update:pageIndex`(0)，两个事件各触发一次请求。
 * 因此调用方的 `update:pageSize` 处理器只应更新本地状态、**不要自己发请求**，
 * 由 `update:pageIndex` 那一次统一查询；否则一次改页大小会打两遍。
 */
function onSizeChange(size: number): void {
  prefs.setPageSize(size)
  emit('update:pageSize', size)
  emit('update:pageIndex', 0)
}
</script>

<template>
  <!--
    宽屏：三段式布局（左右等宽的 1fr + 中间 auto），中间那组永远落在视觉中线，
    且不随每页条数下拉的文案长度变化——`justify-between` 做不到这一点。

    窄屏：两行。
      第一行：翻页 + 页码（居中，独占整行）
      第二行：总数（靠左）+ 每页条数（靠右）

    ⚠️ 用 `grid` 而不是 `flex-wrap`：换行后一行只有一个元素时，
    `justify-between` 会把它推到行首，与宽屏「居中」的读感不一致；
    网格能精确指定每一格的位置。
  -->
  <div
    class="grid grid-cols-[1fr_auto] items-center gap-x-2 gap-y-2 border-t border-line-subtle px-4 py-3 sm:grid-cols-[1fr_auto_1fr] sm:gap-x-3 sm:px-5"
  >
    <!-- 切页 + 调页 + 总数（宽屏）。EP 内置，含页码按钮、当前页高亮、跳页输入框。
         文案（"前往 N 页"、"共 N 条"）走 el-config-provider 的 locale，
         已在 main.ts 跟随 i18n locale 切换，这里不重复翻译。

         ⚠️ `!justify-center`：EP 的 `.el-pagination` 自带 `justify-content: flex-end`，
         且 EP 的样式表排在 `virtual:uno.css` 之后——同特异度下 EP 赢，
         不加 `!` 的话窄屏的分页按钮会靠右而不是居中。 -->
    <el-pagination
      class="col-span-2 !justify-center sm:col-span-1 sm:col-start-2"
      :current-page="currentPage"
      :page-size="effectivePageSize"
      :total="total"
      :disabled="disabled"
      :layout="paginationLayout"
      :pager-count="pagerCount"
      size="small"
      background
      @current-change="onPageChange"
    />

    <!--
      窄屏的总数：EP 的 `total` 段在窄屏被摘掉了（见上方 `paginationLayout`），
      总数不能跟着一起消失——「这一页一共多少条」是分页条的基本信息。
    -->
    <span v-if="isNarrow" class="text-xs text-content-muted">
      {{ t('common.totalItems', { total }) }}
    </span>

    <!-- 每页条数：贴右对齐，与翻页组分开。
         EP 的 sizes 标签写死了「N 条/页」，塞不进「全部」，故独立成 el-select。 -->
    <el-select
      :model-value="pageSize"
      :disabled="disabled"
      size="small"
      class="!w-28 justify-self-end sm:col-start-3"
      :aria-label="t('common.pageSize')"
      @update:model-value="onSizeChange(Number($event))"
    >
      <el-option
        v-for="size in sizeOptions"
        :key="size"
        :value="size"
        :label="t('common.pageSizeOption', { size })"
      />
      <el-option :value="PAGE_SIZE_ALL" :label="allOptionLabel" />
    </el-select>
  </div>
</template>
