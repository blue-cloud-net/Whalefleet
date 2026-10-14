import { defineStore } from 'pinia'

export type ThemeMode = 'light' | 'dark' | 'system'
export type ThemeColor = 'blue' | 'green' | 'purple'

const COLOR_KEY: Record<ThemeColor, string> = {
  blue: 'theme-blue',
  green: 'theme-green',
  purple: 'theme-purple'
}

/**
 * 主题 store：模式（light/dark/system）+ 主色（blue/green/purple）。
 * 持久化到 localStorage，避免刷新丢状态。
 */
export const useThemeStore = defineStore('theme', {
  state: () => ({
    mode: 'system' as ThemeMode,
    color: 'blue' as ThemeColor,
    /** 侧边栏折叠状态 */
    sidebarCollapsed: false
  }),
  getters: {
    /** 实际生效的明亮/暗色（仅在 mode === 'system' 时跟随系统） */
    resolvedMode(state): 'light' | 'dark' {
      if (state.mode !== 'system') return state.mode
      if (typeof window === 'undefined') return 'light'
      return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
    }
  },
  actions: {
    setMode(mode: ThemeMode) {
      this.mode = mode
      this.applyToDocument()
    },
    setColor(color: ThemeColor) {
      this.color = color
      this.applyToDocument()
    },
    toggleSidebar() {
      this.sidebarCollapsed = !this.sidebarCollapsed
    },
    /** 把主题状态反映到 <html>：class="dark theme-{color} lang" */
    applyToDocument() {
      if (typeof document === 'undefined') return
      const html = document.documentElement
      // 暗色
      html.classList.toggle('dark', this.resolvedMode === 'dark')
      // 主色
      html.classList.remove('theme-blue', 'theme-green', 'theme-purple')
      html.classList.add(COLOR_KEY[this.color])
    }
  },
  persist: {
    key: 'whalefleet:theme',
    storage: localStorage
  }
})