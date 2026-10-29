/**
 * 交互式终端（0.8.0）的 URL 与协议帧构造。
 *
 * ## 为什么单独成模块
 * 这段逻辑只有十几行，但**每一行都有一个会静默出错的坑**：
 * - 协议要与页面同源（HTTPS 页面连 `ws://` 会被浏览器直接拦掉，且不报有用的错）
 * - `token` 是 base64url，本身不含特殊字符，但仍要走 `URLSearchParams` 编码——
 *   手工拼字符串时一旦将来令牌格式变化（比如加了 `.` 之外的填充），
 *   症状是「后端说令牌无效」，而肉眼看不出差异
 * - resize 是**文本帧**里的 JSON，终端数据是二进制帧，两者不能混
 *
 * 抽出来之后它可以脱离组件单测（jsdom 里连不了真的 WebSocket）。
 */

/** 与 `window.location` 同形的最小接口（便于单测注入）。 */
export interface TerminalLocationLike {
  /** 页面协议，`https:` 或 `http:` */
  protocol: string
  /** 主机（含端口） */
  host: string
}

/** 建立终端连接所需的信息。 */
export interface TerminalSocketTarget {
  hostId: string
  /** 服务端签发的 terminal token */
  token: string
  /** 初始列数（xterm 的 FitAddon 算出来的）；非正数/省略时不带 */
  columns?: number
  /** 初始行数；同上 */
  rows?: number
}

/** 后端终端端点的固定路径段（与 `TerminalRoutes.ContainerExec` 保持一致）。 */
const TERMINAL_PATH = '/ws/hosts'
const TERMINAL_SUFFIX = '/containers/exec'

/**
 * 构造终端 WebSocket 的完整 URL。
 *
 * @param target 主机、令牌与初始尺寸。
 * @param location 页面位置（默认取 `window.location`）。
 * @returns 形如 `wss://host/ws/hosts/local/containers/exec?token=…&cols=120&rows=30` 的地址。
 * @remarks
 * **路径里的 `hostId` 要编码**：主机名虽是 `[A-Za-z0-9._-]`，但编码成本为零，
 * 而漏掉它一旦将来放开字符集就是线上问题。
 * **`token` 交给 `URLSearchParams`**：它会做正确的百分号编码，
 * 手拼字符串是本仓库已经踩过的坑（见 AGENTS.md 陷阱 12 的同类错误）。
 */
export function buildTerminalSocketUrl(
  target: TerminalSocketTarget,
  location: TerminalLocationLike = window.location
): string {
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:'
  const params = new URLSearchParams({ token: target.token })

  if (target.columns !== undefined && target.columns > 0) {
    params.set('cols', String(Math.trunc(target.columns)))
  }

  if (target.rows !== undefined && target.rows > 0) {
    params.set('rows', String(Math.trunc(target.rows)))
  }

  const path = `${TERMINAL_PATH}/${encodeURIComponent(target.hostId)}${TERMINAL_SUFFIX}`

  return `${scheme}//${location.host}${path}?${params.toString()}`
}

/**
 * 构造 resize 控制帧。
 *
 * @param columns 列数。
 * @param rows 行数。
 * @returns 可直接 `send()` 的文本帧。
 * @remarks
 * 控制帧用**文本帧**、终端数据用**二进制帧**：两个方向按帧类型区分，
 * 服务端因此不需要在字节流里猜「这是用户输入还是控制指令」。
 * 尺寸非正数时不构造（返回 `null`），调用方跳过发送——
 * 给守护进程发 `0` 会让它直接回 400，而界面只表现为「全屏程序画歪了」。
 */
export function buildResizeFrame(columns: number, rows: number): string | null {
  const cols = Math.trunc(columns)
  const lines = Math.trunc(rows)

  if (!Number.isFinite(cols) || !Number.isFinite(lines) || cols <= 0 || lines <= 0) {
    return null
  }

  return JSON.stringify({ type: 'resize', cols, rows: lines })
}

/**
 * 把「会话结束」翻译成 i18n key。
 *
 * @param code 本地判定的结束原因（不是 WebSocket 的 close code）。
 * @returns 可翻译的 key。
 * @remarks
 * 浏览器拿到的 close code 几乎总是 **1006**（异常关闭，且**没有** close 帧），
 * 因为服务端在握手前的拒绝、反代掉线、容器退出在客户端看来长得一模一样。
 * 因此这里不按 close code 猜，只按「连接是否曾建立成功」给出两种说法：
 * 连都没连上 → 多半是令牌过期或容器状态变了；连上过 → 会话结束了。
 */
export function terminalCloseKey(connected: boolean): string {
  return connected ? 'container.terminal.closed' : 'container.terminal.connectFailed'
}
