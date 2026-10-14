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
  }>(),
  {
    tone: 'default',
    size: 'small',
    loading: false,
    disabled: false,
    placement: 'top'
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
  >
    <!-- 这层 span 是功能必需的，不是为了样式：见文件头第 2 条 -->
    <span class="inline-flex">
      <el-button
        text
        circle
        :size="size"
        :type="tone"
        :loading="loading"
        :disabled="disabled"
        :aria-label="label"
        @click="emit('click', $event)"
      >
        <span :class="icon" aria-hidden="true" />
      </el-button>
    </span>
  </el-tooltip>
</template>