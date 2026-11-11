/**
 * 列表行内迷你折线图（sparkline）的**缩放与坐标**计算（0.12.0）。
 *
 * ## 为什么单独成文件
 * 这些是**纯函数**，要脱离组件单测：折线是不是贴着底边、越界的点有没有被夹住、
 * 只有一个点的时候该不该画——这些写在模板里就完全没法断言了。
 *
 * ## 为什么要显式给上限
 * 早先的实现自己取 `Math.max(...values)` 当上限，于是**每一行的纵轴都不一样**：
 * 一个恒定 0.5% 的容器与一个在 40%–60% 之间震荡的容器，画出来是两条一模一样的曲线。
 * 那样的图只能看出「有没有起伏」，看不出「处于什么水位」——而用户要看的是后者。
 *
 * 现在上限由调用方给：
 * - CPU → 100（可超过 100 时取实际峰值，多核并行会让它超）；
 * - 内存 → 100（占上限的百分比）；
 * - 网络 → 峰值（速率没有天然上限）。
 *
 * 再配一层**外壳**（见 `MetricSparkline.vue` 的边框），
 * 「框顶 = 上限、框底 = 0」这件事才有视觉载体。
 */

/** 迷你图的逻辑宽度（viewBox 单位，不是像素）。 */
export const SPARK_WIDTH = 100

/** 迷你图的逻辑高度（viewBox 单位，不是像素）。 */
export const SPARK_HEIGHT = 16

/**
 * 取一组数值里的最大值。
 *
 * 忽略 `null` 与非有限值（首帧速率就是 `null`，那不是 0）。
 *
 * @param values 采样值；`null` 表示这一帧没有数据。
 * @returns 最大值；全为空时返回 0。
 */
export function peakOf(values: readonly (number | null)[]): number {
  let peak = 0

  for (const value of values) {
    if (value !== null && Number.isFinite(value) && value > peak) peak = value
  }

  return peak
}

/**
 * 把采样序列换算成 SVG `polyline` 的 `points` 属性。
 *
 * 纵轴：`0` 在底边、`max` 在顶边，**越界值夹到边界**（不画到框外面去）。
 * 横轴：把 `values.length` 个点等距铺满整宽（含首尾两点）。
 *
 * @param values 采样值（旧 → 新）；`null` 按 0 画（与「这一帧没有流量」的直觉一致）。
 * @param max 纵轴上限；`<= 0` 时退化成序列峰值（再不行就 1），不会除以 0。
 * @param width 逻辑宽度。
 * @param height 逻辑高度。
 * @returns `points` 字符串；**有效点少于 2 个时返回空串**（一个点画不出线）。
 */
export function toPolyline(
  values: readonly (number | null)[],
  max: number,
  width = SPARK_WIDTH,
  height = SPARK_HEIGHT
): string {
  const numeric = values.filter((value): value is number => value !== null && Number.isFinite(value))

  // 一个点连不成线；零个点更不必说。返回空串让调用方只画基线。
  if (numeric.length < 2) return ''

  const safeMax = max > 0 ? max : peakOf(values) > 0 ? peakOf(values) : 1
  const step = width / (values.length - 1)

  return values
    .map((value, index) => {
      // 夹到 0–1：高于上限的采样（CPU 多核、内存统计滞后）画不出框，
      // 但也不能让它把整条曲线的比例带歪。
      const ratio = Math.min(1, Math.max(0, (value ?? 0) / safeMax))
      const x = index * step
      const y = height - ratio * height

      return `${x.toFixed(2)},${y.toFixed(2)}`
    })
    .join(' ')
}
