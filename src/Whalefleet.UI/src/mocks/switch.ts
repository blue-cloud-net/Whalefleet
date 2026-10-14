

import { isScenario } from './scenarios'

/**
 * mock 开关判定。
 *
 * 两个来源（任一满足即开启）：
 * 1. **环境变量** `VITE_ENABLE_MOCK=true` —— 写在 `.env.development.local`。
 *    Vite **只在启动时读 `.env*`**，所以改它必须重启 dev server。
 * 2. **URL 参数** `?mock=on` 或 `?mock=<场景名>` —— 免重启，
 *    场景由 `scenarios.ts` 解析。localStorage 开关仅供程序化调用。
 */

/** 运行时开关的 localStorage 键。 */
export const MOCK_STORAGE_KEY = 'dm:mock'



/**
 * 是否启用 mock（**不含 DEV 判断**）。
 *
 * ## 调用点必须自己写 `import.meta.env.DEV &&` 才有生产隔离效果
 *
 * 若把 DEV 判断写在本函数内部，Vite 无法静态消除调用，
 * `await import('./mocks/browser')` 仍会被打进产物（实测多出一个 260KB 的 chunk）。
 * Vite 只对**调用点表达式里出现的 `import.meta.env.DEV`** 做常量折叠，
 * 因此正确写法是：
 *
 * ```ts
 * if (import.meta.env.DEV && shouldUseMock()) { await import('./mocks/browser') }
 * ```
 *
 * 本函数保留 `dev` 参数仅为可单测，生产路径不会用到它。
 */
export function shouldUseMock(
  env: string | undefined = import.meta.env.VITE_ENABLE_MOCK,
  runtime?: string | null,
  search = typeof window === 'undefined' ? '' : window.location.search
): boolean {
  if (env === 'true') return true
  return isMockRuntimeEnabled(runtime, search)
}

/**
 * 运行时开关判定。
 *
 * 接受 `?mock=on` **或任意合法场景名**（`?mock=counts` 等）。
 * 早先只认 `'on'`，导致 README 里写的「URL 加 `?mock=<场景>`」实际不生效——
 * 文档与实现不符的典型例子。
 */
export function isMockRuntimeEnabled(
  runtime?: string | null,
  search = typeof window === 'undefined' ? '' : window.location.search
): boolean {
  const fromQuery = new URLSearchParams(search).get('mock')
  if (fromQuery === 'on') return true
  if (isScenario(fromQuery)) return true

  const stored =
    runtime ?? (typeof window === 'undefined' ? null : safeGet(MOCK_STORAGE_KEY))
  return stored === 'on'
}

function safeGet(key: string): string | null {
  try {
    return window.localStorage.getItem(key)
  } catch {
    // 隐私模式 / 配额禁用时不可用——读取失败不该阻断应用启动
    return null
  }
}

/** 设置运行时开关（供设置页或控制台调用；值传 `'on'` 开启、传 `null` 清除）。 */
export function setMockRuntime(value: 'on' | null): void {
  try {
    if (value === null) window.localStorage.removeItem(MOCK_STORAGE_KEY)
    else window.localStorage.setItem(MOCK_STORAGE_KEY, value)
  } catch {
    // 同上：localStorage 不可用时静默失败
  }
}