import { watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useThemeStore, type ThemeColor, type ThemeMode } from '@/stores/theme'

/**
 * 主题 composable：包一层 store + 监听系统主题变化（mode === 'system' 时）。
 */
export function useTheme() {
  const store = useThemeStore()
  const { mode, color, resolvedMode, sidebarCollapsed } = storeToRefs(store)

  // 跟随系统：监听 prefers-color-scheme
  if (typeof window !== 'undefined') {
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const onChange = () => {
      if (store.mode === 'system') store.applyToDocument()
    }
    mq.addEventListener('change', onChange)
  }

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