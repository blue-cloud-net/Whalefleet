import { defineStore } from 'pinia'

/** 列表每页条数可选值（与 ui-guidelines 的分页约定一致） */
export const PAGE_SIZE_OPTIONS = [10, 20, 50, 100] as const

export type PageSize = (typeof PAGE_SIZE_OPTIONS)[number]

/**
 * 用户偏好 store：目前只有「列表每页条数」。
 * 主题（stores/theme.ts）与语言（locales/index.ts）各自独立持久化，不在此重复。
 */
export const usePreferencesStore = defineStore('preferences', {
  state: () => ({
    /** 列表每页条数，默认 20 */
    pageSize: 20 as PageSize
  }),
  actions: {
    setPageSize(size: number) {
      // 防御非法值：外部（持久化数据被手改）传入时回落到默认
      this.pageSize = (PAGE_SIZE_OPTIONS as readonly number[]).includes(size)
        ? (size as PageSize)
        : 20
    }
  },
  persist: {
    key: 'docker-manager:preferences',
    storage: localStorage
  }
})