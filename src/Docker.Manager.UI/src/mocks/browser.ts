import { setupWorker } from 'msw/browser'
import { handlers, onUnhandledRequest, resolveScenario, setMockScenario } from './index'

/**
 * 浏览器端 mock worker 启动入口。
 *
 * 由 `main.ts` 在 **`app.mount()` 之前**动态 import 并 await —— 顺序很重要：
 * 若先挂载再启动 worker，首批请求会漏网（真实打到后端），表现为
 * 「刷新一下就好了」的随机故障。
 */
export async function startMockWorker(): Promise<void> {
  // query 优先于环境变量，便于分享链接 / 截图指定场景
  const scenario = resolveScenario(
    window.location.search,
    import.meta.env.VITE_MOCK_SCENARIO
  )
  setMockScenario(scenario)

  const worker = setupWorker(...handlers)
  await worker.start({
    onUnhandledRequest,
    // 日志交给顶栏的 MOCK 角标，避免刷屏；排查时手动去掉 quiet 即可
    quiet: true,
    serviceWorker: { url: '/mockServiceWorker.js' }
  })

  const ready = await waitUntilIntercepting()
  window.__MOCK__ = {
    enabled: true,
    scenario,
    ready
  }

  if (!ready) {
    // 不静默：此时首个业务请求会漏到真实后端，用户看到的是「加载失败」而非原因。
    console.error(
      '[mock] Service Worker 尚未接管当前页面，mock 请求可能漏到真实后端。' +
        '这是 Service Worker 首次注册的固有行为——请刷新一次页面。'
    )
  }
}

/** 探测用的健康检查路径（不对外暴露语义，只用于确认拦截已生效）。 */
const PING_PATH = '/__mock__/ping'

/**
 * 轮询探测，直到**确认 SW 真的在拦截**才返回。
 *
 * ## 为什么不能只看 `navigator.serviceWorker.controller`
 *
 * 实测（远程工作区 + 端口转发）：controller 已是 truthy，但首个业务请求的
 * `Response.fromServiceWorker()` 仍为 `false`——页面并未真正被 SW 接管，
 * 请求直接落到 Vite 代理，后端没起时表现为 `500 text/plain`，
 * 页面显示「主机列表加载失败」，而 MOCK 角标却是亮的，极难定位。
 *
 * 因此改为**用一次真实探测请求来验证**，而不是相信 controller 标志。
 * 这也是唯一能确认「拦截真的生效」的方式。
 */
async function waitUntilIntercepting(timeoutMs = 5000): Promise<boolean> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try {
      const res = await fetch(PING_PATH, { cache: 'no-store' })
      if (res.ok && res.headers.get('x-mock') === '1') return true
    } catch {
      // SW 未接管时请求会漏到 Vite（返回 500）或直接失败，两者都视为「还没就绪」
    }
    await new Promise((r) => setTimeout(r, 150))
  }
  return false
}

declare global {
  interface Window {
    __MOCK__?: { enabled: boolean; scenario: string; ready: boolean }
  }
}