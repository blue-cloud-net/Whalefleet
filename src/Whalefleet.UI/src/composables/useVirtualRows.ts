import { ref, type Ref } from 'vue'
import { createVirtualScroller, type VirtualScroller, type VirtualWindow } from '@/utils/virtual-rows'

/**
 * 虚拟滚动的 Vue 封装（0.5.0）。
 *
 * ## 为什么要包一层
 * `utils/virtual-rows.ts` 是**纯计算**（可脱离组件单测），
 * 这一层只做两件事：把「行数变化」翻译成「追加 / 前插 / 重置」，
 * 以及把滚动位置喂进去、把窗口吐出来。
 *
 * ## 行高模式的切换
 * 「换行」开关一换，所有行的高度都变了 → 已实测的高度全部作废，
 * 必须 `reset`（重建滚动器）。否则会一直用旧高度算位置，滚动会跳。
 */

/** 虚拟滚动参数。 */
export interface UseVirtualRowsOptions {
  /** 视口高度（px，响应式）。 */
  viewportHeight: Ref<number>
  /** 固定行高（px）；固定模式下的行高。 */
  rowHeight: number
  /** 估算行高（px）；返回 `null` 表示固定行高模式。 */
  estimatedRowHeight: () => number | null
  /** 视口外额外渲染的行数。 */
  overscan?: number
}

/**
 * 虚拟滚动。
 *
 * @param options 参数。
 * @returns 窗口与操作。
 */
export function useVirtualRows(options: UseVirtualRowsOptions) {
  const scrollTop = ref(0)
  const view = ref<VirtualWindow>({ startIndex: 0, endIndex: 0, offsetY: 0, totalHeight: 0 })

  const overscan = options.overscan ?? 12
  let scroller = createScroller()
  let count = 0

  /** 重建滚动器（模式变化或重置时）。 */
  function createScroller(): VirtualScroller {
    return createVirtualScroller({
      rowHeight: options.rowHeight,
      estimatedRowHeight: options.estimatedRowHeight()
    })
  }

  /** 重算窗口。 */
  function recompute(): void {
    view.value = scroller.window(scrollTop.value, options.viewportHeight.value, overscan)
  }

  /**
   * 重置（首次加载 / 行高模式变化）。
   *
   * @param nextCount 新的行数。
   */
  function reset(nextCount: number): void {
    scroller = createScroller()
    count = nextCount
    scroller.setCount(nextCount)
    recompute()
  }

  /**
   * 尾部追加。
   *
   * @param added 追加的行数。
   */
  function append(added: number): void {
    if (added <= 0) return
    scroller.append(added)
    count += added
    recompute()
  }

  /**
   * 顶部插入。
   *
   * @param inserted 插入的行数。
   */
  function prepend(inserted: number): void {
    if (inserted <= 0) return
    scroller.prepend(inserted)
    count += inserted
    recompute()
  }

  /**
   * 实测某行高度。
   *
   * @param index 行索引。
   * @param height 实测高度（px）。
   */
  function measure(index: number, height: number): void {
    if (scroller.measure(index, height)) {
      recompute()
    }
  }

  /**
   * 滚动位置变化。
   *
   * @param top 新的滚动位置（px）。
   */
  function onScroll(top: number): void {
    scrollTop.value = top
    recompute()
  }

  return {
    view,
    scrollTop,
    reset,
    append,
    prepend,
    measure,
    onScroll,
    get count(): number {
      return count
    }
  }
}
