/**
 * 虚拟滚动的纯计算（0.5.0）。
 *
 * ## 为什么单独成文件
 * 滚动窗口的数学是**纯逻辑**：没有 DOM、没有 Vue、没有计时器。
 * 放在 `utils/` 里可以脱离组件单测——「往上插入 N 行之后锚点还在不在」
 * 这类问题用组件测要搭一整套 DOM，又慢又不准。
 *
 * ## 两种模式
 * - **固定行高**：不换行时每行高度一致（同一字体、同一行距），
 *   窗口是纯算术，O(1)。
 * - **可变行高**：换行时一行可能占 1～N 屏。未实测的行按**估算行高**占位，
 *   实测后修正。于是需要「前 k 行高度之和」（前缀和）。
 *
 * 前缀和用**增量维护**而不是每次重算：
 * - 追加行 → 只在尾部续写（O(追加数)）
 * - 顶部插入行 → 索引整体后移，必须重建（O(总行数)，但很少发生）
 * - 实测某行 → 后缀整体加一个差值（O(总行数 - 该行)）
 */

/** 渲染窗口。 */
export interface VirtualWindow {
  /** 首个渲染行索引（含）。 */
  startIndex: number
  /** 末个渲染行索引（不含）。 */
  endIndex: number
  /** 渲染区相对内容顶部的偏移（px）。 */
  offsetY: number
  /** 内容总高度（px）。 */
  totalHeight: number
}

/** 空窗口。 */
export const EMPTY_WINDOW: VirtualWindow = { startIndex: 0, endIndex: 0, offsetY: 0, totalHeight: 0 }

/**
 * 虚拟滚动器。
 *
 * 用工厂函数而不是 class：避免 `this` 绑定问题，
 * 也让「内部状态」不会被外部意外改到。
 */
export interface VirtualScroller {
  /** 当前行数。 */
  readonly count: number
  /** 固定行高模式（true）还是可变行高模式（false）。 */
  readonly fixed: boolean
  /**
   * 设置总行数。
   *
   * @param count 新的行数。
   */
  setCount(count: number): void
  /**
   * 尾部追加若干行。
   *
   * @param count 追加的行数。
   */
  append(count: number): void
  /**
   * 顶部插入若干行（向上加载更早的日志）。
   *
   * 已实测的行高随索引一起后移——插入后老行仍占原来的高度。
   *
   * @param count 插入的行数。
   */
  prepend(count: number): void
  /**
   * 实测某行高度。
   *
   * @param index 行索引。
   * @param height 实测高度（px）。
   * @returns 高度是否发生了变化（变化时调用方需要重算窗口）。
   */
  measure(index: number, height: number): boolean
  /**
   * 计算渲染窗口。
   *
   * @param scrollTop 滚动位置（px）。
   * @param viewportHeight 视口高度（px）。
   * @param overscan 视口外额外渲染的行数。
   * @returns 渲染窗口。
   */
  window(scrollTop: number, viewportHeight: number, overscan: number): VirtualWindow
}

/**
 * 创建一个虚拟滚动器。
 *
 * @param options 配置。
 * @param options.rowHeight 固定行高（px）；固定模式下的行高。
 * @param options.estimatedRowHeight 估算行高（px）。**传 `null` 表示固定行高模式**。
 * @returns 滚动器。
 */
export function createVirtualScroller(options: {
  rowHeight: number
  estimatedRowHeight?: number | null
}): VirtualScroller {
  const estimated = options.estimatedRowHeight ?? null
  const fixed = estimated === null
  const rowHeight = options.rowHeight

  let count = 0
  /** 实测行高（仅可变模式）；0 表示未测量。 */
  let heights: number[] = []
  /** 前缀和：`prefix[k]` = 前 k 行高度之和；`prefix[0] = 0`。 */
  let prefix: number[] = [0]

  /** 取第 index 行的实际高度（未测量时用估算值）。 */
  function heightAt(index: number): number {
    if (fixed) return rowHeight
    const measured = heights[index] ?? 0
    return measured > 0 ? measured : (estimated as number)
  }

  /** 重建前缀和（O(n)）。 */
  function rebuild(): void {
    if (fixed) {
      prefix = [0]
      return
    }

    const next: number[] = new Array(count + 1)
    next[0] = 0
    for (let i = 0; i < count; i += 1) {
      next[i + 1] = (next[i] ?? 0) + heightAt(i)
    }
    prefix = next
  }

  /** 同步前缀和的长度与内容（追加 / 截断时用）。 */
  function syncPrefix(): void {
    if (fixed) {
      prefix = [0]
      return
    }

    // 截断：prefix 只需保留到 count。
    if (prefix.length > count + 1) {
      prefix.length = count + 1
      return
    }

    // 追加：只在尾部续写。
    while (prefix.length <= count) {
      const index = prefix.length - 1
      prefix.push((prefix[index] ?? 0) + heightAt(index))
    }
  }

  return {
    get count(): number {
      return count
    },

    get fixed(): boolean {
      return fixed
    },

    setCount(next: number): void {
      const target = Math.max(0, Math.floor(next))
      if (target === count) return

      if (target < count) {
        heights.length = target
        count = target
        syncPrefix()
        return
      }

      for (let i = count; i < target; i += 1) {
        heights.push(0)
      }
      count = target
      syncPrefix()
    },

    append(added: number): void {
      if (added <= 0) return
      for (let i = 0; i < added; i += 1) {
        heights.push(0)
      }
      count += added
      syncPrefix()
    },

    prepend(added: number): void {
      if (added <= 0) return
      if (fixed) {
        count += added
        return
      }

      const zeros: number[] = new Array(added).fill(0)
      heights = zeros.concat(heights)
      count += added
      rebuild()
    },

    measure(index: number, height: number): boolean {
      if (fixed) return false
      if (index < 0 || index >= count) return false
      if (!Number.isFinite(height) || height <= 0) return false
      if (heights[index] === height) return false

      const delta = height - heightAt(index)
      heights[index] = height

      // 后缀整体平移。
      for (let k = index + 1; k <= count; k += 1) {
        prefix[k] = (prefix[k] ?? 0) + delta
      }

      return true
    },

    window(scrollTop: number, viewportHeight: number, overscan: number): VirtualWindow {
      if (count === 0) return EMPTY_WINDOW

      const top = Math.max(0, scrollTop)
      const height = Math.max(0, viewportHeight)
      const pad = Math.max(0, overscan)

      if (fixed) {
        const totalHeight = count * rowHeight
        const first = Math.min(count - 1, Math.floor(top / rowHeight))
        const visible = Math.ceil(height / rowHeight) + 1

        const startIndex = clamp(first - pad, 0, count)
        const endIndex = clamp(first + visible + pad, 0, count)

        return { startIndex, endIndex, offsetY: startIndex * rowHeight, totalHeight }
      }

      const totalHeight = prefix[count] ?? 0
      const first = findRowAt(prefix, top, count)
      const last = findRowAt(prefix, top + height, count)

      const startIndex = clamp(first - pad, 0, count)
      const endIndex = clamp(last + 1 + pad, 0, count)

      return { startIndex, endIndex, offsetY: prefix[startIndex] ?? 0, totalHeight }
    }
  }
}

/**
 * 找到偏移量 `offset` 落在哪一行。
 *
 * @param prefix 前缀和（`prefix[k]` = 前 k 行高度之和）。
 * @param offset 垂直偏移（px）。
 * @param count 行数。
 * @returns 行索引（`0 … count-1`）。
 */
export function findRowAt(prefix: number[], offset: number, count: number): number {
  if (count <= 0) return 0
  if (offset <= 0) return 0

  // 找最大的 i ∈ [0, count-1] 使 prefix[i] <= offset。
  let low = 0
  let high = count - 1
  let result = 0

  while (low <= high) {
    const mid = (low + high) >> 1
    if ((prefix[mid] ?? 0) <= offset) {
      result = mid
      low = mid + 1
    } else {
      high = mid - 1
    }
  }

  return result
}

/**
 * 把数值夹在区间内。
 *
 * @param value 值。
 * @param min 下界。
 * @param max 上界。
 * @returns 夹紧后的值。
 */
function clamp(value: number, min: number, max: number): number {
  if (value < min) return min
  if (value > max) return max
  return value
}
