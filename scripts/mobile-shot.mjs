/**
 * 移动端视口截图脚本（开发期辅助工具，不参与生产构建）。
 *
 * 用 CDP（Chrome DevTools Protocol）驱动 headless Chrome，
 * 以指定视口宽度渲染页面并截图 —— 用来在**真实窄屏**下检查布局，
 * 而不是靠推测「窄屏会不会挤」。
 *
 * 用法：
 *   node scripts/mobile-shot.mjs <url> <out.png> [width] [height] [fullPage]
 *
 * 例：
 *   node scripts/mobile-shot.mjs http://localhost:30123/hosts/local/volumes shots/volumes-390.png 390 844
 *
 * ⚠️ 需要一个开着 `--remote-debugging-port` 的 Chrome：
 *   docker run -d -p 9333:9222 chromedp/headless-shell:latest
 *   然后设置 CDP_PORT=9333（默认 9333）。
 *
 * ⚠️ 用 Node 22 内置的**全局** `WebSocket`，不引 `ws` 包：
 * 开发机的 node_modules 顶层软链可能断在网络共享上（`ws` 解析不到），
 * 而全局 WebSocket 是 Node 22 起就有的标准能力。
 */

import { writeFileSync } from 'node:fs'

const [, , url, out, widthArg, heightArg, fullPageArg] = process.argv

if (!url || !out) {
  console.error('用法：node scripts/mobile-shot.mjs <url> <out.png> [width] [height] [fullPage]')
  process.exit(1)
}

const width = Number(widthArg ?? 390)
const height = Number(heightArg ?? 844)
const fullPage = fullPageArg === 'full'
const port = process.env.CDP_PORT ?? '9333'

const res = await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent(url)}`, {
  method: 'PUT'
})
const target = await res.json()
const ws = new WebSocket(target.webSocketDebuggerUrl)

let nextId = 0
const pending = new Map()

ws.addEventListener('message', (event) => {
  const msg = JSON.parse(event.data)
  const entry = pending.get(msg.id)
  if (!entry) return
  pending.delete(msg.id)
  if (msg.error) entry.reject(new Error(JSON.stringify(msg.error)))
  else entry.resolve(msg.result)
})

function send(method, params = {}) {
  const id = ++nextId
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    ws.send(JSON.stringify({ id, method, params }))
  })
}

await new Promise((resolve, reject) => {
  ws.addEventListener('open', resolve, { once: true })
  ws.addEventListener('error', reject, { once: true })
})

await send('Emulation.setDeviceMetricsOverride', {
  width,
  height,
  deviceScaleFactor: 2,
  mobile: true
})
await send('Page.enable')
await send('Runtime.enable')

// 登录态：页面若跳到 /login，用环境变量里的令牌直接写入 localStorage 后重载
//
// ⚠️ **令牌必须在访问目标 URL 之前写入**：
// 先打开目标页 → 未登录 → 路由守卫把它踢到 /login（URL 变了）→
// 此时再 reload 只会停在 /login，登录后落到默认落地页（总览），
// 截出来根本不是要看的那个页面。
// 正确顺序：先打开同源的一个页面写入 localStorage，再导航到目标 URL。
if (process.env.WF_TOKEN) {
  const origin = new URL(url).origin
  await send('Page.navigate', { url: `${origin}/login` })
  await new Promise((r) => setTimeout(r, 1200))
  await send('Runtime.evaluate', {
    expression: `localStorage.setItem('whalefleet:auth', ${JSON.stringify(
      JSON.stringify({
        accessToken: process.env.WF_TOKEN,
        refreshToken: process.env.WF_REFRESH ?? '',
        user: {
          id: '90a265b7-e6f5-4393-b2b8-e1ee1f186e6b',
          username: 'admin',
          roles: ['admin'],
          permissions: [],
          mustChangePassword: false
        }
      })
    )})`
  })
}

await send('Page.navigate', { url })
await new Promise((r) => setTimeout(r, 3000))

if (fullPage) {
  const { cssContentSize } = await send('Page.getLayoutMetrics')
  await send('Emulation.setDeviceMetricsOverride', {
    width,
    height: Math.ceil(cssContentSize.height),
    deviceScaleFactor: 2,
    mobile: true
  })
  await new Promise((r) => setTimeout(r, 600))
}

const { data } = await send('Page.captureScreenshot', { format: 'png' })
writeFileSync(out, Buffer.from(data, 'base64'))

/**
 * 顺带量一下**横向溢出**与关键元素的实际宽度。
 *
 * 移动端最常见的残留问题是「某一处比视口宽」——截图上看着只是被裁掉，
 * 但页面其实能横向拖动，表格随之被拖出视野。
 * `scrollWidth > clientWidth` 就是页面级横向溢出的判据。
 */
const metrics = await send('Runtime.evaluate', {
  expression: `JSON.stringify({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
    bodyScrollWidth: document.body.scrollWidth,
    overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    brand: (() => {
      const el = document.querySelector('header button span.font-semibold')
      if (!el) return null
      const r = el.getBoundingClientRect()
      return { text: el.textContent.trim(), width: Math.round(r.width), scrollWidth: el.scrollWidth, truncated: el.scrollWidth > el.clientWidth + 1 }
    })(),
    toolbar: (() => {
      const bar = document.querySelector('.app-list-toolbar__filters')?.parentElement
      if (!bar) return null
      const kids = Array.from(bar.children).map(el => {
        const r = el.getBoundingClientRect()
        return {
          tag: el.tagName,
          cls: String(el.className || '').slice(0, 60),
          x: Math.round(r.x),
          y: Math.round(r.y),
          w: Math.round(r.width),
          h: Math.round(r.height),
          text: (el.textContent || '').trim().slice(0, 18)
        }
      })
      return { barWidth: Math.round(bar.getBoundingClientRect().width), kids }
    })(),
    widest: Array.from(document.querySelectorAll('body *'))
      .map(el => ({ el, r: el.getBoundingClientRect() }))
      .filter(x => x.r.width > document.documentElement.clientWidth + 1)
      .slice(0, 5)
      .map(x => ({
        tag: x.el.tagName,
        cls: (x.el.className && x.el.className.baseVal !== undefined ? x.el.className.baseVal : String(x.el.className || '')).slice(0, 80),
        width: Math.round(x.r.width)
      }))
  })`,
  returnByValue: true
})

console.log(`saved ${out} (${width}x${height}${fullPage ? ' full' : ''})`)
if (metrics.result?.value) {
  console.log('metrics:', metrics.result.value)
}

await fetch(`http://127.0.0.1:${port}/json/close/${target.id}`)
ws.close()
process.exit(0)
