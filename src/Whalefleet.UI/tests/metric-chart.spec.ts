import { describe, expect, it } from 'vitest'
import { peakOf, toPolyline, SPARK_HEIGHT, SPARK_WIDTH } from '../src/utils/metric-chart'

/**
 * 行内迷你图的缩放计算。
 *
 * ## 为什么要守这些分支
 * 这些函数决定了「一条曲线在框里的高度」，而错误的缩放**不会报错**，
 * 只会让图变得没有意义：
 * - 上限取错 → 一个恒定 0.5% 的容器与一个在 40%–60% 震荡的容器画出同样的曲线；
 * - 越界不夹 → 多核 CPU 的 250% 会把整条曲线压扁成贴底的一条直线；
 * - 一个点也画线 → 得到一条零长度的 polyline，看起来像「没有数据」和「数据是 0」的混合体。
 */
describe('peakOf', () => {
  it('取最大值', () => {
    expect(peakOf([1, 5, 3])).toBe(5)
  })

  it('忽略 null（首帧速率就是 null，它不是 0）', () => {
    expect(peakOf([null, 4, null, 2])).toBe(4)
  })

  it('全空时返回 0', () => {
    expect(peakOf([])).toBe(0)
    expect(peakOf([null, null])).toBe(0)
  })

  it('忽略非有限值', () => {
    expect(peakOf([Number.NaN, Number.POSITIVE_INFINITY, 3])).toBe(3)
  })
})

describe('toPolyline', () => {
  it('有效点少于 2 个时返回空串（一个点连不成线）', () => {
    expect(toPolyline([], 100)).toBe('')
    expect(toPolyline([null], 100)).toBe('')
    expect(toPolyline([5], 100)).toBe('')
    expect(toPolyline([5, null], 100)).toBe('')
  })

  it('铺满整宽：首点在 0、末点在 width', () => {
    const points = toPolyline([0, 0, 0], 100).split(' ')

    expect(points).toHaveLength(3)
    expect(points[0]?.split(',')[0]).toBe((0).toFixed(2))
    expect(points[2]?.split(',')[0]).toBe(SPARK_WIDTH.toFixed(2))
  })

  it('0 贴底边、等于上限时贴顶边', () => {
    const [flat] = toPolyline([0, 0], 100).split(' ')
    const [, peak] = toPolyline([100, 100], 100).split(' ')

    expect(flat?.split(',')[1]).toBe(SPARK_HEIGHT.toFixed(2))
    expect(peak?.split(',')[1]).toBe((0).toFixed(2))
  })

  it('上限固定时，高低水位画出不同高度（这正是「裸图看不出水位」的修法）', () => {
    // 同样是「平坦的线」，一个在 10%、一个在 80%——纵轴固定 100 时必须分得开
    const low = toPolyline([10, 10], 100).split(' ')[0]?.split(',')[1]
    const high = toPolyline([80, 80], 100).split(' ')[0]?.split(',')[1]

    expect(Number(low)).toBeGreaterThan(Number(high))
  })

  it('超过上限的值夹到顶边，不影响其它点的比例', () => {
    // 多核 CPU 会超过 100%
    const points = toPolyline([250, 250], 100).split(' ')

    for (const point of points) {
      expect(point.split(',')[1]).toBe((0).toFixed(2))
    }
  })

  it('负值夹到底边', () => {
    expect(toPolyline([-50, -50], 100).split(' ')[0]?.split(',')[1]).toBe(
      SPARK_HEIGHT.toFixed(2)
    )
  })

  it('max 非正时退化成峰值，不会除以 0', () => {
    const points = toPolyline([10, 20], 0)

    expect(points).not.toBe('')
    // 峰值那个点贴顶边
    expect(points.split(' ')[1]?.split(',')[1]).toBe((0).toFixed(2))
  })

  it('null 按 0 画（这一帧没有流量）', () => {
    // 注意：至少要 2 个有效点才画线，所以这里用两个有效点 + 一个 null
    const points = toPolyline([null, 50, 50], 100).split(' ')

    expect(points[0]?.split(',')[1]).toBe(SPARK_HEIGHT.toFixed(2))
  })
})
