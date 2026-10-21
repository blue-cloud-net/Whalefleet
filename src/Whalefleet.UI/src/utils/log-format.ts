/**
 * 日志行与资源数值的展示格式化（0.5.0）。
 *
 * ## 为什么要单独成文件
 * 这些都是**纯函数**，且要出现在多个地方（虚拟滚动的行、指标卡、通知文案）。
 * 放在 `utils/` 里可以脱离组件单测——「1.5 MB 还是 1536 KB」这种事
 * 写在模板里就没法断言了。
 */

/**
 * 把字节数格式化成人类可读的形式。
 *
 * @param bytes 字节数。
 * @param digits 小数位（默认 1）。
 * @returns 形如 `1.5 MB` 的文本。
 */
export function formatBytes(bytes: number, digits = 1): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'

  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  let value = bytes
  let unit = 0

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }

  // 字节本身不需要小数位（`512.0 B` 很怪）。
  const rounded = unit === 0 ? String(Math.round(value)) : value.toFixed(digits)

  return `${rounded} ${units[unit]}`
}

/**
 * 把 CPU 百分比格式化。
 *
 * @param percent 百分比（0–100×核数）。
 * @returns 形如 `12.3%` 的文本。
 */
export function formatPercent(percent: number, digits = 1): string {
  if (!Number.isFinite(percent)) return '—'
  return `${percent.toFixed(digits)}%`
}

/**
 * 把内存占用格式化成「已用 / 上限」。
 *
 * 上限为 0（守护进程没设限）时只显示已用部分——**不能显示成「0 / 0」**，
 * 那会被读成「确实没占内存」。
 *
 * @param usage 已用字节数。
 * @param limit 上限字节数。
 * @returns 形如 `128 MB / 512 MB` 的文本。
 */
export function formatMemory(usage: number, limit: number): string {
  const used = formatBytes(usage)
  return limit > 0 ? `${used} / ${formatBytes(limit)}` : used
}

/**
 * 把内存占用算成百分比（用于进度条）。
 *
 * @param usage 已用字节数。
 * @param limit 上限字节数。
 * @returns 0–100 的百分比；上限未知时返回 `null`。
 */
export function memoryPercent(usage: number, limit: number): number | null {
  if (limit <= 0 || !Number.isFinite(usage)) return null
  return Math.min(100, Math.max(0, (usage / limit) * 100))
}

/**
 * 把 ISO 时间戳压成「时:分:秒」。

 * 日志行上要显示的是**时刻**而不是完整日期——一屏日志里几乎都是同一天，
 * 完整日期只会把每行撑长。
 *
 * @param timestamp ISO 8601 时间戳；`null` 表示守护进程没给。
 * @returns 形如 `22:04:11` 的文本；无时间戳时返回空串。
 */
export function formatLogTime(timestamp: string | null): string {
  if (!timestamp) return ''

  const parsed = new Date(timestamp)
  if (Number.isNaN(parsed.getTime())) return ''

  const pad = (value: number): string => String(value).padStart(2, '0')

  return `${pad(parsed.getHours())}:${pad(parsed.getMinutes())}:${pad(parsed.getSeconds())}`
}

/**
 * 把网络字节数格式化成「↓ 接收 / ↑ 发送」。
 *
 * @param rx 接收字节数。
 * @param tx 发送字节数。
 * @returns 形如 `↓ 1.2 MB / ↑ 340 KB` 的文本。
 */
export function formatNetwork(rx: number, tx: number): string {
  return `↓ ${formatBytes(rx)} / ↑ ${formatBytes(tx)}`
}
