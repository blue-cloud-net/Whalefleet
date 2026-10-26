import { onBeforeUnmount, ref, type Ref } from 'vue'

/**
 * 列表页的关键字搜索（输入去抖 + 回车即时）。
 *
 * ## 为什么要有两种触发方式
 * - **输入即搜（去抖）**：用户连续打字时不该每敲一个字就发一次请求——
 *   十个字就是十次往返，而且中间几次的结果还会互相覆盖（响应乱序）。
 * - **回车即搜**：用户打完关键字直接回车时，等满去抖窗口才出结果会显得「没反应」。
 *   回车必须**立刻**执行，并顺手撤掉还没触发的定时器（否则会连着搜两次）。
 *
 * ## 为什么不自己写计时器
 * 「去抖 + 立即触发 + 组件卸载时清理」这组合有三种边界情况（连点清空、
 * 回车与定时器同时到点、卸载时还有未触发的定时器），
 * 每个列表页各写一遍必然有一页漏掉其中一个。收在这里只写一次。
 */

/** 去抖窗口（毫秒）。 */
const DEFAULT_DEBOUNCE_MS = 300

/**
 * 创建一个关键字搜索器。
 *
 * @param run 真正执行搜索的回调（通常是把页码归零后重新拉列表）
 * @param debounceMs 去抖窗口
 * @returns 关键字与两个触发方法
 */
export function useListSearch(
  run: () => void | Promise<void>,
  debounceMs: number = DEFAULT_DEBOUNCE_MS
): {
  /** 搜索框的值 */
  keyword: Ref<string>
  /** 输入时用：去抖后触发 */
  schedule: () => void
  /** 回车 / 清空时用：立即触发，并撤掉未触发的定时器 */
  searchNow: () => void
} {
  const keyword = ref('')
  let timer: ReturnType<typeof setTimeout> | null = null

  /** 撤掉还没触发的定时器。 */
  function cancel(): void {
    if (timer !== null) {
      clearTimeout(timer)
      timer = null
    }
  }

  /** 输入时用：去抖后触发。 */
  function schedule(): void {
    cancel()

    timer = setTimeout(() => {
      timer = null
      void run()
    }, debounceMs)
  }

  /** 回车 / 清空时用：立即触发。 */
  function searchNow(): void {
    cancel()
    void run()
  }

  // 组件卸载时必须清掉定时器：否则它会在页面已经走了之后触发一次搜索，
  // 往一个已经不在屏幕上的组件写数据。
  onBeforeUnmount(cancel)

  return { keyword, schedule, searchNow }
}
