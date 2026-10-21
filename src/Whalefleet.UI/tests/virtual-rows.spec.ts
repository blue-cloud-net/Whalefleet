import { describe, expect, it } from 'vitest'
import { createVirtualScroller, findRowAt } from '../src/utils/virtual-rows'

/**
 * 虚拟滚动的纯计算测试。
 *
 * 重点是三件事：
 * 1. **窗口不越界**（startIndex ≥ 0、endIndex ≤ count、start ≤ end）；
 * 2. **追加 / 前插后位置仍然对**（行高索引没串）；
 * 3. **实测行高后总高度会变**（否则滚动条长度是错的）。
 */
describe('固定行高模式', () => {
  it('窗口落在内容范围内', () => {
    const scroller = createVirtualScroller({ rowHeight: 20 })
    scroller.setCount(1000)

    const view = scroller.window(0, 400, 10)

    expect(view.startIndex).toBe(0)
    expect(view.endIndex).toBeGreaterThan(0)
    expect(view.endIndex).toBeLessThanOrEqual(1000)
    expect(view.totalHeight).toBe(20000)
  })

  it('滚动时窗口跟着下移，偏移量等于首行之前的高度', () => {
    const scroller = createVirtualScroller({ rowHeight: 20 })
    scroller.setCount(1000)

    const view = scroller.window(2000, 400, 0)

    expect(view.startIndex).toBe(100)
    expect(view.offsetY).toBe(2000)
  })

  it('overscan 让窗口上下各多渲染一些行', () => {
    const scroller = createVirtualScroller({ rowHeight: 20 })
    scroller.setCount(1000)

    const view = scroller.window(2000, 400, 5)

    expect(view.startIndex).toBe(95)
    expect(view.endIndex).toBeGreaterThan(120)
  })

  it('空内容返回空窗口', () => {
    const scroller = createVirtualScroller({ rowHeight: 20 })

    const view = scroller.window(0, 400, 10)

    expect(view.startIndex).toBe(0)
    expect(view.endIndex).toBe(0)
    expect(view.totalHeight).toBe(0)
  })

  it('滚到底部时窗口不越界', () => {
    const scroller = createVirtualScroller({ rowHeight: 20 })
    scroller.setCount(10)

    const view = scroller.window(10000, 400, 10)

    expect(view.startIndex).toBeGreaterThanOrEqual(0)
    expect(view.endIndex).toBe(10)
    expect(view.endIndex).toBeGreaterThanOrEqual(view.startIndex)
  })
})

describe('可变行高模式', () => {
  it('未测量的行按估算行高占位', () => {
    const scroller = createVirtualScroller({ rowHeight: 20, estimatedRowHeight: 20 })
    scroller.setCount(100)

    expect(scroller.window(0, 400, 0).totalHeight).toBe(2000)
  })

  it('实测行高后总高度随之变化', () => {
    const scroller = createVirtualScroller({ rowHeight: 20, estimatedRowHeight: 20 })
    scroller.setCount(100)

    // 第 0 行实际占 3 行高（换行后的长行）
    expect(scroller.measure(0, 60)).toBe(true)

    expect(scroller.window(0, 400, 0).totalHeight).toBe(2040)
  })

  it('同一行重复实测同一高度不触发重算', () => {
    const scroller = createVirtualScroller({ rowHeight: 20, estimatedRowHeight: 20 })
    scroller.setCount(10)

    expect(scroller.measure(0, 40)).toBe(true)
    expect(scroller.measure(0, 40)).toBe(false)
  })

  it('非法高度（0 / 负数 / NaN）被忽略', () => {
    const scroller = createVirtualScroller({ rowHeight: 20, estimatedRowHeight: 20 })
    scroller.setCount(10)

    expect(scroller.measure(0, 0)).toBe(false)
    expect(scroller.measure(0, -5)).toBe(false)
    expect(scroller.measure(0, Number.NaN)).toBe(false)
    expect(scroller.measure(99, 40)).toBe(false)
  })

  it('前插的行把已实测的高度整体后移（老行仍占原来的高度）', () => {
    const scroller = createVirtualScroller({ rowHeight: 20, estimatedRowHeight: 20 })
    scroller.setCount(10)
    scroller.measure(0, 100)

    scroller.prepend(5)

    // 5 行新行（估算 20）+ 原来的第 0 行（实测 100）+ 9 行估算
    expect(scroller.window(0, 400, 0).totalHeight).toBe(5 * 20 + 100 + 9 * 20)
  })

  it('追加的行不改变已有行的位置', () => {
    const scroller = createVirtualScroller({ rowHeight: 20, estimatedRowHeight: 20 })
    scroller.setCount(10)
    scroller.measure(0, 100)

    const before = scroller.window(0, 400, 0).totalHeight
    scroller.append(5)
    const after = scroller.window(0, 400, 0).totalHeight

    expect(after - before).toBe(5 * 20)
  })
})

describe('findRowAt', () => {
  const prefix = [0, 20, 40, 60, 80]

  it('落在某一行内部时返回该行', () => {
    expect(findRowAt(prefix, 0, 4)).toBe(0)
    expect(findRowAt(prefix, 25, 4)).toBe(1)
    expect(findRowAt(prefix, 79, 4)).toBe(3)
  })

  it('负偏移夹到第一行', () => {
    expect(findRowAt(prefix, -100, 4)).toBe(0)
  })

  it('超出末尾时夹到最后一行', () => {
    expect(findRowAt(prefix, 10000, 4)).toBe(3)
  })

  it('空内容返回 0', () => {
    expect(findRowAt(prefix, 10, 0)).toBe(0)
  })
})
