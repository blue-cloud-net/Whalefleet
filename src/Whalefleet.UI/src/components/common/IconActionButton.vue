<script setup lang="ts">
/**
 * 行内图标动作按钮：图标 + 悬浮提示。
 *
 * ## 为什么要抽这个组件（而不是每个页面各写一遍）
 * 表格行里的动作按钮一旦图标化，就必须同时具备三样东西，缺一不可：
 *  1. **文字标签收进 tooltip** —— 图标本身说不清是「重启」还是「重新加载」；
 *  2. **`<span>` 包裹** —— 按钮 loading / disabled 时不派发 `mouseenter`，
 *     直接把 `el-tooltip` 挂在 `el-button` 上，操作进行中 tooltip 就永远弹不出来，
 *     用户只剩一个没有说明的转圈；
 *  3. **`aria-label`** —— 图标按钮没有可见文字，屏幕阅读器读不出它是干什么的。
 * 三样乘以每页四五个动作，就是几十行几乎相同的标记。抽出来后「图标 + 文案 + 颜色」
 * 由调用方一行说清，包裹逻辑只写一次。
 *
 * ## ⚠️ 新增图标必须同步 safelist
 * `icon` 是**动态字符串**（`:class="icon"`），UnoCSS 的静态提取器扫不到，
 * 必须写进 `uno.config.ts` 的 safelist，否则运行时表现为「按钮在、图标不见」。
 * 和路由 `meta.icon` 是同一个坑，见那里的 `collectRouteIcons` 与 `assertIconsExist`
 * （后者会在 dev/build 期直接抛错，所以名字写错不会静默溜过去）。
 */
withDefaults(
  defineProps<{
    /** Carbon 图标类名，如 `i-carbon-trash-can`；必须是 safelist 里已有的名字 */
    icon: string
    /** 悬浮提示文案，同时作为无障碍标签 */
    label: string
    tone?: 'default' | 'danger'
    size?: 'default' | 'small'
    /** 执行中：转圈并禁用 */
    loading?: boolean
    disabled?: boolean
    /**
     * 禁用原因提示：按钮 `disabled` 时用它替代 `label` 作为 tooltip 内容。
     *
     * 用于「按钮看得见但当前点不了」的场景（例如不能删除自己那一行）——
     * 这时只说「删除」等于让人对着一个永远点不动的按钮猜原因。
     * `aria-label` 始终保持 `label`，因为无障碍名称该是**动作**，
     * 禁用原因由 tooltip 补充说明即可。
     */
    hint?: string
    /** tooltip 方位；默认顶部——行内按钮的常规位置 */
    placement?: 'top' | 'bottom' | 'left' | 'right'
    /**
     * 显示文字标签（图标 + 文字），用于工具条上的批量动作。
     *
     * 行内动作只给图标是刻意的：一行里四五个图标再配上文字会把表格撑得很宽。
     * 但**工具条上的批量动作相反**——它是「这一页支持批量操作」的唯一提示，
     * 只给图标时用户得逐个悬停才知道哪个是启动、哪个是删除，
     * 而它们恰恰是高频动作，值不得让人去猜。
     *
     * 开启后：按钮变成**标准的 el-button**（`plain`：有边框、有底色），
     * 而不是行内那种无边框的文字按钮——
     * 工具条上的批量动作是「可以点的按钮」，行内动作是「表格里的小图标」，
     * 两者用同一套外观会让用户分不清哪个是正式动作。
     *
     * 且 `label` 的 tooltip **不再弹**（文字已经写在按钮上，再弹一遍是噪音），
     * 只有 `disabled` + `hint` 时仍弹——禁用原因只能靠 tooltip 说。
     */
    showLabel?: boolean
  }>(),
  {
    tone: 'default',
    size: 'small',
    loading: false,
    disabled: false,
    placement: 'top',
    showLabel: false
  }
)

const emit = defineEmits<{ click: [event: MouseEvent] }>()

/**
 * tooltip 延迟：相邻几个按钮鼠标横扫过去时，无延迟会连成一片闪。
 * 300ms 足够用户在某个按钮上停稳再判断要不要读提示。
 */
const SHOW_AFTER = 300
</script>

<template>
  <el-tooltip
    :content="disabled && hint ? hint : label"
    :placement="placement"
    :show-after="SHOW_AFTER"
    :disabled="showLabel && !(disabled && hint)"
  >
    <!-- 这层 span 是功能必需的，不是为了样式：见文件头第 2 条 -->
    <span class="inline-flex">
      <!--
        ⚠️ `text` 与 `plain` 互斥：行内动作用 `text`（无边框的纯图标），
        工具条上的批量动作用 `plain`（标准带框按钮）——两套外观区分
        「表格里的小图标」与「工具条上可以点的正式动作」。
      -->
      <el-button
        :text="!showLabel"
        :plain="showLabel"
        :circle="!showLabel"
        :size="size"
        :type="tone"
        :loading="loading"
        :disabled="disabled"
        :aria-label="label"
        @click="emit('click', $event)"
      >
        <!--
          ⚠️ 图标必须放在 `icon` 插槽里（不是默认插槽）：EP 在 `loading` 时
          会用转圈图标**替换** `icon` 插槽的内容，两个都渲染就成了「转圈 + 图标」。
        -->
        <template #icon><span :class="icon" aria-hidden="true" /></template>
        <template v-if="showLabel">{{ label }}</template>
      </el-button>
    </span>
  </el-tooltip>
</template>