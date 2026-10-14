<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
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
    三段式布局而不是一个 justify-between：
    `justify-between` 只按两端对齐，中间那组控件的位置取决于左右两段的宽度，
    每页条数下拉的宽度一变（"10 条/页" vs "全部"），整组翻页按钮就会横向漂移。
    改成「三列网格 + 两侧等宽」后，中间那组永远落在正中，且不随文案长度变化。
  -->
  <div
    class="grid grid-cols-[1fr_auto_1fr] items-center gap-2 sm:gap-3 border-t border-line-subtle px-4 py-3 sm:px-5"
  >
    <!-- 左列留空占位，保证中间那组真正居中（grid 的 auto 列按内容宽，两侧 1fr 平分剩余） -->
    <span />

    <!-- 切页 + 调页 + 总数：EP 内置，含页码按钮、当前页高亮、跳页输入框。
         文案（"前往 N 页"、"共 N 条"）走 el-config-provider 的 locale，
         已在 main.ts 跟随 i18n locale 切换，这里不重复翻译。 -->
    <el-pagination
      :current-page="currentPage"
      :page-size="effectivePageSize"
      :total="total"
      :disabled="disabled"
      layout="prev, pager, next, jumper, total"
      size="small"
      background
      @current-change="onPageChange"
    />

    <!-- 每页条数：贴右对齐，与翻页组分开。
         EP 的 sizes 标签写死了「N 条/页」，塞不进「全部」，故独立成 el-select。 -->
    <el-select
      :model-value="pageSize"
      :disabled="disabled"
      size="small"
      class="!w-28 justify-self-end"
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
