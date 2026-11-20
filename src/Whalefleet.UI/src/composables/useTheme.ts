import { watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useThemeStore, type ThemeColor, type ThemeMode } from '@/stores/theme'

/**
 * 主题 composable：包一层 store + 监听系统主题变化（mode === 'system' 时）。
 */
export function useTheme() {
  const store = useThemeStore()
  const { mode, color, resolvedMode, sidebarCollapsed } = storeToRefs(store)

  // 跟随系统：监听 prefers-color-scheme。
  //
  // 监听器放在 store 里（`bindSystemThemeListener`），**不在这里**：
  // 本 composable 会被顶栏、登录页等多个组件调用，
  // 放这儿等于每次调用都挂一个监听器，且谁也不负责摘。
  // store 里用 state 上的闸门保证全局只有一个。
  store.bindSystemThemeListener()

  // 任何主题相关状态变化 → 同步到 <html>
  watch([mode, color, resolvedMode], () => store.applyToDocument())

  return {
    mode,
    color,
    resolvedMode,
    sidebarCollapsed,
    setMode: (m: ThemeMode) => store.setMode(m),
    setColor: (c: ThemeColor) => store.setColor(c),
    toggleSidebar: () => store.toggleSidebar()
  }
}
