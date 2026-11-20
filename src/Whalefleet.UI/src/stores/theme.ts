import { defineStore } from 'pinia'

export type ThemeMode = 'light' | 'dark' | 'system'
export type ThemeColor = 'blue' | 'green' | 'purple'

const COLOR_KEY: Record<ThemeColor, string> = {
  blue: 'theme-blue',
  green: 'theme-green',
  purple: 'theme-purple'
}

/**
 * 持久化键。
 *
 * ⚠️ `index.html` 里的首屏内联脚本也要读这个键 —— 两处必须一致，
 * 改了这里就要同步改那里，否则首屏按旧键解析（表现是「刷新后主题闪一下才变对」）。
 */
export const THEME_STORAGE_KEY = 'whalefleet:theme'

/** 系统暗色偏好的媒体查询。 */
const DARK_SCHEME_QUERY = '(prefers-color-scheme: dark)'

/** 品牌色十六进制：只用于 `<meta name="theme-color">`（移动端浏览器地址栏底色）。 */
const BRAND_HEX: Record<ThemeColor, string> = {
  blue: '#409EFF',
  green: '#67C23A',
  purple: '#7A40B8'
}

/** 暗色下的页面底色：与 `main.css` 的 `--surface-page` 保持一致。 */
const DARK_PAGE_COLOR = '#0d121c'

/** 读当下的系统暗色偏好。 */
function readSystemDark(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false
  return window.matchMedia(DARK_SCHEME_QUERY).matches
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
    sidebarCollapsed: false,
    /**
     * 系统当前是不是暗色。
     *
     * ⚠️ **必须是 state，不能在 getter 里现读 `matchMedia`**：
     * getter 是 computed，只在它依赖的**响应式**数据变化时重算；
     * `matchMedia` 不是响应式，页面不刷新它就永远是旧值。
     * 于是「跟随系统」在系统切到夜间后依然停在亮色 —— 而浏览器那边
     * （移动端 Chrome 的强制暗色）已经把页面反相了，
     * 结果就是**中性灰底 + 文字被压成灰块 + 图标变成实心方块**的鬼样子。
     *
     * 把系统偏好搬进 state，监听器负责更新它，`resolvedMode` 才有得跟随。
     */
    systemDark: readSystemDark(),
    /**
     * 系统主题监听是否已绑定。
     *
     * 放 state 而不是模块级变量：闸门跟着 store 实例走，
     * 一个应用（一个 pinia）只有一个 store，也就只会有一个监听器；
     * 而 `useTheme()` 会被顶栏 / 登录页等多个组件调用，
     * 每次调用都 `addEventListener` 会叠加出 N 个监听器（且永不移除），
     * 于是「切一次系统主题」触发 N 次重绘。
     */
    systemListenerBound: false
  }),
  getters: {
    /** 实际生效的明亮/暗色（仅在 mode === 'system' 时跟随系统） */
    resolvedMode(state): 'light' | 'dark' {
      if (state.mode !== 'system') return state.mode
      return state.systemDark ? 'dark' : 'light'
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
    /**
     * 绑定系统主题变化监听（**整个应用只绑一次**）。
     *
     * 放在 store 而不是 `useTheme()` 里：composable 会被多个组件调用，
     * 放那儿等于每个组件一个监听器；这里由 state 里的闸门保证唯一。
     */
    bindSystemThemeListener() {
      if (this.systemListenerBound) return
      if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return
      this.systemListenerBound = true

      const mq = window.matchMedia(DARK_SCHEME_QUERY)
      const onChange = (event: MediaQueryListEvent) => this.setSystemDark(event.matches)

      if (typeof mq.addEventListener === 'function') {
        mq.addEventListener('change', onChange)
      } else if (typeof mq.addListener === 'function') {
        // 老 WebView / iOS Safari < 14 只有 addListener
        mq.addListener(onChange)
      }
    },
    /** 系统暗色偏好变了：更新 state，并在「跟随系统」时立刻反映到 <html>。 */
    setSystemDark(dark: boolean) {
      if (this.systemDark === dark) return
      this.systemDark = dark
      if (this.mode === 'system') this.applyToDocument()
    },
    /** 把主题状态反映到 <html>：class="dark theme-{color} lang" */
    applyToDocument() {
      if (typeof document === 'undefined') return
      const html = document.documentElement
      const dark = this.resolvedMode === 'dark'
      // 暗色
      html.classList.toggle('dark', dark)
      // 主色
      html.classList.remove('theme-blue', 'theme-green', 'theme-purple')
      html.classList.add(COLOR_KEY[this.color])
      // 「跟随系统」态也留个 class：文档里写明了这个契约，
      // CSS 可以据此决定 color-scheme 与首屏行为
      html.classList.toggle('theme-system', this.mode === 'system')
      this.syncThemeColorMeta(dark)
    },
    /**
     * 同步 `<meta name="theme-color">`。
     *
     * 移动端浏览器用它给地址栏 / 状态栏上色——不跟着切的话，
     * 暗色页面顶上会留一条亮蓝色横条，是「夜间模式没做全」最显眼的一处。
     */
    syncThemeColorMeta(dark: boolean) {
      if (typeof document === 'undefined') return
      const meta = document.querySelector('meta[name="theme-color"]')
      if (!meta) return
      meta.setAttribute('content', dark ? DARK_PAGE_COLOR : BRAND_HEX[this.color])
    }
  },
  persist: {
    key: THEME_STORAGE_KEY,
    storage: localStorage,
    /**
     * 只持久化「用户的偏好」。
     *
     * `systemDark` 是**当下的系统状态**，落盘后必然过期（下次打开时是旧值），
     * 所以不能进持久化 —— 它每次启动都由 `readSystemDark()` 重新读。
     */
    pick: ['mode', 'color', 'sidebarCollapsed']
  }
})
