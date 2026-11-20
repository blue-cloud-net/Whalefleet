import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import piniaPluginPersistedstate from 'pinia-plugin-persistedstate'
import { useThemeStore } from '../src/stores/theme'

/**
 * 主题 store —— 「跟随系统」必须真的跟得上。
 *
 * ## 为什么这个文件存在
 *
 * 曾经的实现把系统偏好写在 getter 里现读 `matchMedia`：
 *
 * ```ts
 * resolvedMode(state) {
 *   if (state.mode !== 'system') return state.mode
 *   return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
 * }
 * ```
 *
 * Pinia 的 getter 是 **computed**：只在它依赖的响应式数据变化时重算。
 * `matchMedia` 不是响应式，于是页面开着的时候系统切到夜间，
 * `resolvedMode` 仍返回旧值 ——「跟随系统」名存实亡。
 *
 * 更糟的是它在移动端会**放大**症状：应用还停在亮色，
 * 而浏览器（Chrome / Edge）看系统是暗色、页面又没声明支持暗色，
 * 便自作主张给整页套一层「强制暗色」——
 * 结果是中性灰底、文字被压成灰块、`mask` 图标变成实心方块。
 *
 * 现在系统偏好搬进了 state（`systemDark`），本文件守住这条链路。
 */

type Listener = (event: MediaQueryListEvent) => void

interface FakeMediaQuery {
  mql: MediaQueryList
  /** 模拟系统切换（触发 change 事件） */
  set: (matches: boolean) => void
  /** 当前注册了几个监听 —— 用来断言「只绑一次」 */
  listenerCount: () => number
}

function createFakeMediaQuery(initial: boolean): FakeMediaQuery {
  const listeners = new Set<Listener>()
  // `MediaQueryList.matches` 在 TS 里是**只读**属性，
  // 所以真值放 state 里，mql 用 getter 把它露出去。
  const state = { matches: initial }
  const mql = {
    get matches() {
      return state.matches
    },
    media: '(prefers-color-scheme: dark)',
    onchange: null,
    addEventListener: (_type: string, cb: Listener) => {
      listeners.add(cb)
    },
    removeEventListener: (_type: string, cb: Listener) => {
      listeners.delete(cb)
    },
    // 老 WebView / iOS Safari < 14 只有这一对 API
    addListener: (cb: Listener) => {
      listeners.add(cb)
    },
    removeListener: (cb: Listener) => {
      listeners.delete(cb)
    },
    dispatchEvent: () => true
  } as unknown as MediaQueryList

  return {
    mql,
    set: (matches: boolean) => {
      state.matches = matches
      const event = { matches, media: mql.media } as MediaQueryListEvent
      listeners.forEach((cb) => cb(event))
    },
    listenerCount: () => listeners.size
  }
}

/**
 * 拿一个**全新的** store 实例。
 *
 * 每个用例都建新的 pinia → 新的 store → 新的「监听已绑定」闸门，
 * 所以「只绑一次」这条断言在用例之间互不干扰。
 *
 * ⚠️ 不要用 `vi.resetModules()` + 动态 import 造「新实例」：
 * 它会把 `pinia` 也重置成另一份，于是静态 import 的 `setActivePinia`
 * 设的不是 store 所在那份 pinia 的 activePinia → store 直接抛错。
 */
function freshStore() {
  const pinia = createPinia()
  // ⚠️ **必须把 pinia 装进 app**：Pinia 的 `use()` 在 pinia 尚未安装时
  // 只把插件**放进待安装队列**，真正挂上要等 `app.use(pinia)`。
  // 不装 app 的话持久化插件根本不会生效（症状：`localStorage` 里永远没有东西，
  // 而 `$persist` 是 undefined）。`main.ts` 里也是先 `app.use(pinia)` 再用 store。
  createApp({ render: () => null }).use(pinia)
  // 必须装持久化插件：store 的 `persist` 配置是给它用的，
  // 不装的话「持久化只存偏好」那组用例就成空转。
  pinia.use(piniaPluginPersistedstate)
  setActivePinia(pinia)
  return useThemeStore()
}

beforeEach(() => {
  document.documentElement.className = ''
  localStorage.clear()
})

describe('主题 store —— 跟随系统', () => {
  it('系统是暗色时，解析结果就是暗色并落到 <html> 上', async () => {
    const fake = createFakeMediaQuery(true)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.applyToDocument()

    expect(theme.resolvedMode).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('系统从亮切到暗：页面跟着变暗（这条是回归测试的核心）', async () => {
    const fake = createFakeMediaQuery(false)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.bindSystemThemeListener()
    theme.applyToDocument()
    expect(document.documentElement.classList.contains('dark')).toBe(false)

    fake.set(true)

    expect(theme.resolvedMode).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('系统从暗切到亮：页面跟着变亮', async () => {
    const fake = createFakeMediaQuery(true)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.bindSystemThemeListener()
    theme.applyToDocument()
    expect(document.documentElement.classList.contains('dark')).toBe(true)

    fake.set(false)

    expect(theme.resolvedMode).toBe('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('手动指定亮/暗时，系统怎么变都不动（用户的显式选择优先）', async () => {
    const fake = createFakeMediaQuery(false)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.bindSystemThemeListener()
    theme.setMode('light')
    theme.applyToDocument()

    fake.set(true)

    expect(theme.resolvedMode).toBe('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('「跟随系统」态在 <html> 上留 theme-system class', async () => {
    const fake = createFakeMediaQuery(false)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.applyToDocument()
    expect(document.documentElement.classList.contains('theme-system')).toBe(true)

    theme.setMode('dark')
    expect(document.documentElement.classList.contains('theme-system')).toBe(false)
  })
})

describe('主题 store —— 监听与持久化', () => {
  it('系统监听全局只绑一次（重复调用不会叠加监听器）', async () => {
    const fake = createFakeMediaQuery(false)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.bindSystemThemeListener()
    theme.bindSystemThemeListener()
    theme.bindSystemThemeListener()

    expect(fake.listenerCount()).toBe(1)
  })

  it('systemDark 不进持久化：它是「当下的系统状态」，落盘后必然过期', async () => {
    const fake = createFakeMediaQuery(false)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.bindSystemThemeListener()
    fake.set(true)
    await nextTick()

    const raw = localStorage.getItem('whalefleet:theme')
    expect(raw).not.toBeNull()
    expect(JSON.parse(raw as string)).not.toHaveProperty('systemDark')
    // 用户的偏好照常持久化
    expect(JSON.parse(raw as string)).toMatchObject({ mode: 'system', color: 'blue' })
  })

  it('持久化键与首屏内联脚本约定的一致（index.html 靠它解析）', async () => {
    const fake = createFakeMediaQuery(false)
    vi.stubGlobal('matchMedia', vi.fn(() => fake.mql))

    const theme = freshStore()
    theme.setMode('dark')
    await nextTick()

    expect(localStorage.getItem('whalefleet:theme')).not.toBeNull()
  })
})
