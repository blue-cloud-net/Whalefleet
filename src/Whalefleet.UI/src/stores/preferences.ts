import { defineStore } from 'pinia'

/** 列表每页条数可选值（与 ui-guidelines 的分页约定一致） */
export const PAGE_SIZE_OPTIONS = [10, 20, 50, 100] as const

/**
 * 每页条数上限，**必须与后端 `ImageService.MaxPageSize` / `ContainerService.MaxPageSize` 一致**。
 *
 * 后端对 `pageSize` 做 `Math.Clamp(value, 1, MaxPageSize)`，超过的值会被静默夹紧。
 * 前端若不认这个上限，「全部」在条目超过 100 时只会拿到 100 条，
 * 而界面上没有任何地方说过这件事——用户以为看到了全部。
 * 改后端上限时这里也要改，否则「全部」的语义会与实际返回悄悄脱节。
 */
export const MAX_PAGE_SIZE = 100

/**
 * 「全部」：不分页，一页列出所有条目。
 *
 * ## 为什么用 0 而不是 `total` 或 `-1`
 * - **`total` 不行**：总数为 20 时，"全部"与"20 条/页"完全同值，两者在下拉框里无法区分，
 *   用户选了「全部」却看到显示成「20 条/页」。而一旦之后又新增到 25 条，
 *   他期望的"看到全部"会静默退化成"只看到 20 条"。
 * - **0 天然不与真实档位冲突**：后端 `Math.Clamp(pageSize, 1, 100)` 会把 0 夹成 1，
 *   也就是说 0 作为真实条数永远传不出去，它只可能表达「不限条数」这一个意思。
 *
 * 请求前由 `resolvePageSize()` 换算成真实条数。
 */
export const PAGE_SIZE_ALL = 0

export type PageSize = (typeof PAGE_SIZE_OPTIONS)[number] | typeof PAGE_SIZE_ALL

/**
 * 把界面上的每页条数换算成**可以发给后端的**真实条数。
 *
 * 「全部」（0）要换成"当前总数"——但**不能超过 `MAX_PAGE_SIZE`**：
 * 那正是后端的夹紧线，换算出比它更大的值只会被静默夹掉，
 * 结果是"请求了全部、拿到的却是 100 条"，且前后端都没有任何提示。
 * 夹在上限内至少让请求值与实际返回一致。
 *
 * 下限给 1：空列表时总数为 0，传 0 出去又会被夹成 1，不如直接给 1。
 */
export function resolvePageSize(pageSize: number, total: number): number {
  if (pageSize !== PAGE_SIZE_ALL) return pageSize

  return Math.min(Math.max(total, 1), MAX_PAGE_SIZE)
}

/**
 * 用户偏好 store：目前只有「列表每页条数」。
 * 主题（stores/theme.ts）与语言（locales/index.ts）各自独立持久化，不在此重复。
 */
export const usePreferencesStore = defineStore('preferences', {
  state: () => ({
    /** 列表每页条数，默认 20；`PAGE_SIZE_ALL` 表示「全部」 */
    pageSize: 20 as PageSize
  }),
  actions: {
    setPageSize(size: number) {
      // 防御非法值：外部（持久化数据被手改）传入时回落到默认。
      // `PAGE_SIZE_ALL` 单独放行——它在语义上是合法的，不能被当成"非法值"打回 20。
      this.pageSize =
        size === PAGE_SIZE_ALL || (PAGE_SIZE_OPTIONS as readonly number[]).includes(size)
          ? (size as PageSize)
          : 20
    }
  },
  persist: {
    key: 'whalefleet:preferences',
    storage: localStorage
  }
})