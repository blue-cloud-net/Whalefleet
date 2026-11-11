<script setup lang="ts">
import { computed } from 'vue'
import { SPARK_HEIGHT, SPARK_WIDTH, toPolyline } from '@/utils/metric-chart'

/**
 * 带**外壳**的行内迷你折线图（0.12.0）。
 *
 * ## 为什么要外壳
 * 早先的折线是裸画在单元格里的，纵轴上下限完全不可见：一条贴着顶边的曲线，
 * 既可能是「CPU 打满了」也可能是「这行自身的最大值只有 0.5%」——
 * 因为当时的上限是**每行自己算的峰值**（见 `utils/metric-chart.ts` 的说明）。
 * 用户看到的是形状，读不出水位。
 *
 * 现在两件事一起做：
 * ① 上限由调用方给定（CPU/内存 100，网络取峰值），纵轴位置有了绝对含义；
 * ② 画一层 1px 描边 + 下沉底色的**框**——框顶就是上限、框底就是 0，
 *    再加一条半程虚线，让人一眼看出「这条线在框里的哪个高度」。
 *
 * ## 三种指标共用一个组件
 * 差别只有描边色与上限，为它们各写一份会立刻出现「改了一处漏了一处」。
 *
 * ## 为什么手写 SVG 而不是 ECharts
 * 一页 100 行 × 3 列 = 300 个图表实例，canvas 的内存与初始化成本与收益完全不成比例。
 */
const props = withDefaults(
  defineProps<{
    /** 采样值（旧 → 新）；`null` 表示该帧没有数据。 */
    values: readonly (number | null)[]
    /** 纵轴上限；`<= 0` 时退化成序列峰值。 */
    max: number
    /** 指标种类：只决定描边色。 */
    tone?: 'cpu' | 'memory' | 'network'
    /**
     * 悬浮提示 / 无障碍文本。
     *
     * ⚠️ **必须由调用方给**（含「轴范围」这件事）：框只能表达相对高度，
     * 「框顶到底是 100% 还是 1.2 MB/s」只有调用方知道。
     */
    label?: string
  }>(),
  { tone: 'cpu', label: '' }
)

/**
 * 描边色。
 *
 * ⚠️ 用**语义令牌**而不是 CSS 变量名拼字符串：canvas 之外的地方（这里是 SVG）
 * 虽然能继承 `currentColor`，但颜色本身仍要落在一个真实存在的工具类上——
 * 早先内存那条用的是 `text-surface-accent`，而 `surface` 色板里**没有** `accent`，
 * 于是那个类静默不生效、曲线一直在吃父元素颜色（看不出是 bug）。
 */
const TONE_CLASS: Record<'cpu' | 'memory' | 'network', string> = {
  cpu: 'text-brand-500',
  memory: 'text-status-warn',
  network: 'text-status-ok'
}

const points = computed(() => toPolyline(props.values, props.max))

/** 有效点少于 2 个时画不出线，只留外壳与基线（那本身也是「没有可比较的走势」的信息）。 */
const hasLine = computed(() => points.value !== '')

const toneClass = computed(() => TONE_CLASS[props.tone])
</script>

<template>
  <div
    class="relative h-4 w-full overflow-hidden rounded-sm border border-line-subtle bg-surface-sunken"
    role="img"
    :aria-label="label || undefined"
    :title="label || undefined"
  >
    <svg
      class="h-full w-full"
      :viewBox="`0 0 ${SPARK_WIDTH} ${SPARK_HEIGHT}`"
      preserveAspectRatio="none"
      aria-hidden="true"
    >
      <!--
        半程虚线：只靠外框时，「线在这一半以上还是以下」还得目测，
        这条线把框分成上下两半，读起来快得多。
      -->
      <line
        x1="0"
        :y1="SPARK_HEIGHT / 2"
        :x2="SPARK_WIDTH"
        :y2="SPARK_HEIGHT / 2"
        stroke="currentColor"
        stroke-width="1"
        stroke-dasharray="2 3"
        class="text-content-faint"
        opacity="0.45"
        vector-effect="non-scaling-stroke"
      />

      <!-- 基线（0）：外框底边已经画了线，这条是为了在数据压到 0 时仍能分辨「贴底」与「没画」 -->
      <line
        x1="0"
        :y1="SPARK_HEIGHT"
        :x2="SPARK_WIDTH"
        :y2="SPARK_HEIGHT"
        stroke="currentColor"
        stroke-width="1"
        class="text-content-faint"
        opacity="0.6"
        vector-effect="non-scaling-stroke"
      />

      <!--
        `vector-effect="non-scaling-stroke"` 不能省：viewBox 被 `preserveAspectRatio="none"`
        横向拉伸后，线宽会跟着放大到几像素，看着像一条粗带子。
      -->
      <polyline
        v-if="hasLine"
        :points="points"
        fill="none"
        stroke="currentColor"
        stroke-width="1"
        stroke-linejoin="round"
        stroke-linecap="round"
        vector-effect="non-scaling-stroke"
        :class="toneClass"
      />
    </svg>
  </div>
</template>
